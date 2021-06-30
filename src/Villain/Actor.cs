using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Villain
{
    public abstract class Actor<T>
    {
        protected delegate void ActorEvent(CancellationToken token);

        private readonly ChannelReader<T> _channel;
        private CancellationTokenSource _cts;

        protected event ActorEvent OnSetup;
        protected event ActorEvent OnTeardown;
        protected event ActorEvent OnProcessing;

        protected Actor(ChannelReader<T> channel)
        {
            _channel = channel;

            OnSetup += CancelSetup;
            OnTeardown += CancelTeardown;
            OnProcessing += CancelProcessing;
        }

        public ILogger Logger { get; set; }

        public async Task Initialize(CancellationToken token)
        {
            OnSetup?.Invoke(token);

            _cts = new CancellationTokenSource();

            await Task.Factory.StartNew(
                    Consume,
                    token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .ContinueWith(
                    t => PropagateError(t.Unwrap()),
                    TaskContinuationOptions.AttachedToParent);

            Task PropagateError(Task t)
            {
                if (t.IsFaulted || t.IsCanceled)
                {
                    _cts.Dispose();
                    throw t.Exception ?? new Exception($"Initializing actor failed {(t.IsCanceled ? "(Cancelled)" : "")}");
                }

                return Task.CompletedTask;
            }
        }

        public async Task WaitForDispose(CancellationToken token)
        {
            var completeChannel = _channel.Completion;
            var timeout = Task.Delay(Timeout.InfiniteTimeSpan, token);

            await Task.WhenAny(completeChannel, timeout)
                .ContinueWith(_ => Dispose(token), TaskContinuationOptions.None);
        }

        public virtual async ValueTask Dispose(CancellationToken token)
        {
            if (_cts != null)
            {
                await _cts.CancelAsync();
            }

            OnTeardown?.Invoke(token);
        }

        protected abstract Task OnInboxReceived(T data, CancellationToken token);
        protected abstract Task OnError(Exception e, CancellationToken token);
        protected abstract Task OnInboxClosed(CancellationToken token);

        protected virtual TimeSpan ReadTimeout => Timeout.InfiniteTimeSpan;
        protected virtual Task OnInboxTimeout(CancellationToken token) => 
            Task.CompletedTask;

        private static void CancelSetup(CancellationToken token) => 
            token.ThrowIfCancellationRequested();
        private static void CancelProcessing(CancellationToken token) => 
            token.ThrowIfCancellationRequested();
        private static void CancelTeardown(CancellationToken token) => 
            token.ThrowIfCancellationRequested();

        private async Task Consume()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    OnProcessing?.Invoke(_cts.Token);

                    var timeout = new CancellationTokenSource(ReadTimeout);
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeout.Token);

                    switch (await TryReceiveInboxMessage(cts))
                    {
                        case Success success:
                            await OnInboxReceived(success.Data, _cts.Token);
                            break;
                        case Empty _:
                            break;
                        case Error error:
                            await OnError(error.Exception, _cts.Token);
                            break;
                        case TimedOut _:
                            await OnInboxTimeout(_cts.Token);
                            break;
                        case Closed _:
                            await OnInboxClosed(_cts.Token);
                            return; // return, not break
                    }
                }
                catch (OperationCanceledException)
                {
                    // expected
                }
                catch (Exception ex)
                {
                    Logger.Error($"Expected an OperationCanceledException, but found '{ex.Message}'.", ex);
                }
            }

            _cts.Dispose();

            async Task<IChannelResult> TryReceiveInboxMessage(CancellationTokenSource cts)
            {
                try
                {
                    if (await WaitToReadMessage(cts.Token))
                    {
                        return ReadMessage(out var message)
                            ? (IChannelResult)new Success(message)
                            : new Empty();
                    }

                    // channel closed
                    return new Closed();
                }
                catch (OperationCanceledException)
                {
                    return new TimedOut();
                }
                catch (Exception e)
                {
                    return e is ChannelClosedException
                        ? (IChannelResult)new Closed()
                        : new Error(e);
                }
                finally
                {
                    // cancel timeout
                    cts.Dispose();
                }

                async Task<bool> WaitToReadMessage(CancellationToken token) => await _channel.WaitToReadAsync(token);

                bool ReadMessage(out T message) => _channel.TryRead(out message);
            }
        }

        private interface IChannelResult {}

        private class Success : IChannelResult
        {
            public Success(T data)
            {
                Data = data;
            }
            public T Data { get; }
        }
        private class Empty : IChannelResult { }
        private class Closed : IChannelResult { }
        private class TimedOut : IChannelResult { }
        private class Error : IChannelResult
        {
            public Error(Exception exception)
            {
                Exception = exception;
            }
            public Exception Exception { get; }
        }
    }
}
