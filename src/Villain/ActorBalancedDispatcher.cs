using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nito.AsyncEx;

namespace Villain
{
    public interface IActorPublisher<T>
    {
        Task<bool> Enqueue(T data);
        Task<(IEnumerable<T> sent, IEnumerable<T> notSent)> EnqueueBatch(IEnumerable<T> data);
    }

    public interface IActorLifetime : IAsyncDisposable
    {
        Task Reboot();
    }

    public interface IActorFactory<T>
    {
        Actor<T> CreateActor(ChannelReader<T> channel);
    }

    public abstract class ActorBalancedDispatcher<T> : IActorLifetime, IActorPublisher<T>
    {
        private enum State
        {
            Inactive = 0,
            Initializing,
            Active,
            TearingDown
        }

        protected delegate void DispatcherEvent();
        protected delegate void LifetimeEvent(IAutoScaleSignaler autoScaleSignaler);

        private readonly IActorFactory<T> _actorFactory;
        private readonly AsyncLock _rebootLock;
        private readonly AsyncLock _enqueueInitLock;
        private readonly AsyncLock _actorsLock;
        private readonly AsyncReaderWriterLock _lifetimeLock;

        private State _state;
        private Task _initTask;
        private Task _teardownTask;
        private List<Actor<T>> _actors;
        private IAutoScaleSignaler _autoScaleSignaler;

        protected Channel<T> Channel;
        protected event DispatcherEvent OnMessageEnqueue;
        protected event LifetimeEvent OnSetup;
        protected event LifetimeEvent OnTeardown;

        protected ActorBalancedDispatcher(IActorFactory<T> actorFactory)
        {
            _actorFactory = actorFactory;
            _rebootLock = new AsyncLock();
            _enqueueInitLock = new AsyncLock();
            _actorsLock = new AsyncLock();
            _lifetimeLock = new AsyncReaderWriterLock();
            _state = State.Inactive;

            OnSetup += SetupAutoScaleSignaler;
            OnTeardown += TeardownAutoScaleSignaler;
        }

        public ILogger Logger { get; set; }

        public async Task Reboot()
        {
            using var _ = await _rebootLock.LockAsync();

            Logger.Debug($"Rebooting starting for actor dispatcher: {GetType().Name}.");

            await EnsureTeardown();
            await EnsureInitialized();

            Logger.Debug($"Rebooting finished for actor dispatcher: {GetType().Name}.");
        }

        public async Task<bool> Enqueue(T data)
        {
            using var _ = await _lifetimeLock.ReaderLockAsync();

            // must always be initialized before writing to channel
            await EnsureInitialized();

            OnMessageEnqueue?.Invoke();

            return Channel.Writer.TryWrite(data);
        }

        public async Task<(IEnumerable<T> sent, IEnumerable<T> notSent)> EnqueueBatch(IEnumerable<T> data)
        {
            using var _ = await _lifetimeLock.ReaderLockAsync();

            // must always be initialized before writing to channel
            await EnsureInitialized();

            var items = data.Aggregate((new List<T>(), new List<T>()), (result, message) =>
            {
                OnMessageEnqueue?.Invoke();

                var (sent, notSent) = result;
                if (Channel.Writer.TryWrite(message))
                {
                    sent.Add(message);
                }
                else
                {
                    notSent.Add(message);
                }
                return result;
            });

            return items;
        }

        public virtual async ValueTask DisposeAsync()
        {
            await EnsureTeardown();
        }

        protected abstract Channel<T> ChannelFactory { get; }
        protected abstract IAutoScaleSignaler AutoScaleSignalerFactory { get; }

        protected internal async Task<int> GetActorCount()
        {
            using var _ = await _actorsLock.LockAsync();

            return _actors?.Count ?? 0;
        }

        protected internal async void ScaleActors(int count)
        {
            try
            {
                await (count switch
                {
                    { } c when c > 0 => AddActors(),
                    { } c when c < 0 => RemoveActors(),
                    _ => Task.CompletedTask
                });
            }
            catch (Exception e)
            {
                Logger.Error($"Auto scaling failed for actor dispatcher: {GetType().Name}.", e);
            }

            async Task AddActors()
            {
                using var _ = await _actorsLock.LockAsync();

                if (_state != State.Active)
                {
                    LogCancelled();
                    return;
                }

                LogStarting();

                var newActors = Enumerable.Range(0, count)
                    .Select(_ => _actorFactory.CreateActor(Channel))
                    .ToList();

                _actors ??= new List<Actor<T>>();
                _actors.AddRange(newActors);

                await InitializeActors(newActors);

                LogFinished();
            }

            async Task RemoveActors()
            {
                using var _ = await _actorsLock.LockAsync();

                if (_state != State.Active)
                {
                    LogCancelled();
                    return;
                }

                if (_actors == null || !_actors.Any())
                {
                    return;
                }

                LogStarting();

                var absCount = Math.Abs(count);
                var actors = _actors.Take(absCount);
                _actors.RemoveRange(0, absCount);

                // ReSharper disable AccessToDisposedClosure
                using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Task.WhenAll(actors.Select(a => a.Dispose(ct.Token).AsTask()));

                LogFinished();
            }

            void LogCancelled() =>
                Logger.Info($"Auto scaling ({count}) ignored for actor dispatcher: {GetType().Name}. State is {_state}.");
            void LogStarting() =>
                Logger.Info($"Auto scaling ({count}) starting for actor dispatcher: {GetType().Name}.");
            void LogFinished() =>
                Logger.Info($"Auto scaling ({count}) finished for actor dispatcher: {GetType().Name}.");
        }

        private async Task EnsureInitialized()
        {
            await Initialize();

            async Task Initialize()
            {
                using var _ = await _enqueueInitLock.LockAsync();

                try
                {
                    await (_initTask ??= Setup());
                }
                catch
                {
                    _initTask = null;
                    throw;
                }
            }

            async Task Setup()
            {
                var prevState = _state;
                try
                {
                    _state = State.Initializing;
                    await Work();
                    _state = State.Active;
                }
                catch (Exception e)
                {
                    _state = prevState;

                    Logger.Error($"{nameof(Setup)} failed for actor dispatcher: {GetType().Name}.", e);
                    throw;
                }
                finally
                {
                    _teardownTask = null;
                }
            }

            async Task Work()
            {
                Channel = ChannelFactory;

                _autoScaleSignaler = AutoScaleSignalerFactory;
                _autoScaleSignaler.AutoScaleSignaled += ScaleActors;

                OnMessageEnqueue += _autoScaleSignaler.IncrementThroughput;

                await CreateActors();

                OnSetup?.Invoke(_autoScaleSignaler);
            }

            async Task CreateActors()
            {
                using var _ = await _actorsLock.LockAsync();

                _actors = Enumerable.Range(0, _autoScaleSignaler.MinWorkerCount)
                    .Select(_ => _actorFactory.CreateActor(Channel))
                    .ToList();

                await InitializeActors(_actors);
            }
        }

        private async Task EnsureTeardown()
        {
            using var _ = await _lifetimeLock.WriterLockAsync();

            try
            {
                await (_teardownTask ??= Destroy());
            }
            catch
            {
                _teardownTask = null;
                // don't throw, always safe
            }

            async Task Destroy()
            {
                var prevState = _state;
                try
                {
                    _state = State.TearingDown;
                    await Work();
                    _state = State.Inactive;
                }
                catch (Exception e)
                {
                    _state = prevState;

                    Logger.Error($"{nameof(EnsureTeardown)} failed for actor dispatcher: {GetType().Name}.", e);
                    throw;
                }
                finally
                {
                    _initTask = null;
                }
            }

            async Task Work()
            {
                OnTeardown?.Invoke(_autoScaleSignaler);

                if (_autoScaleSignaler != null)
                {
                    OnMessageEnqueue -= _autoScaleSignaler.IncrementThroughput;
                    _autoScaleSignaler.AutoScaleSignaled -= ScaleActors;
                }

                Channel?.Writer.Complete();

                await CleanUpActors();

                _autoScaleSignaler = null;

                Channel = null;
            }

            async Task CleanUpActors()
            {
                using var __ = await _actorsLock.LockAsync();

                if (_actors == null || !_actors.Any())
                {
                    return;
                }

                // ReSharper disable AccessToDisposedClosure
                using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Task.WhenAll(_actors.Select(a => a.WaitForDispose(ct.Token)));

                _actors.Clear();
                _actors = null;
            }
        }

        private static async Task InitializeActors(IEnumerable<Actor<T>> actors)
        {
            // ReSharper disable AccessToDisposedClosure
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Task.WhenAll(actors.Select(a => a.Initialize(ct.Token)));
        }

        private static void SetupAutoScaleSignaler(IAutoScaleSignaler signaler)
        {
            if (signaler is ILifetimeManagedAutoScaleSignaler lifetimeSignaler)
            {
                lifetimeSignaler.Initialize();
            }
        }

        private static void TeardownAutoScaleSignaler(IAutoScaleSignaler signaler)
        {
            if (signaler is ILifetimeManagedAutoScaleSignaler lifetimeSignaler)
            {
                lifetimeSignaler.Dispose();
            }
        }
    }
}
