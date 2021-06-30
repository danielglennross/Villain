using System;
using System.Threading;
using System.Threading.Tasks;

namespace Villain
{
    public delegate void AutoScaleSignaled(int count);

    public interface IAutoScaleSignaler
    {
        event AutoScaleSignaled AutoScaleSignaled;
        void IncrementThroughput();
        int MinWorkerCount { get; }
    }

    public interface ILifetimeManagedAutoScaleSignaler : IDisposable
    {
        void Initialize();
    }

    public class FixedAutoScaleSignaler : IAutoScaleSignaler
    {
        public FixedAutoScaleSignaler(int fixedWorkerCount)
        {
            MinWorkerCount = fixedWorkerCount;
        }

#pragma warning disable 67
        public event AutoScaleSignaled AutoScaleSignaled;
#pragma warning restore 67

        public void IncrementThroughput()
        {
        }

        public int MinWorkerCount { get; }
    }

    public class ActorThroughputAutoScaleSignaler : IAutoScaleSignaler, ILifetimeManagedAutoScaleSignaler
    {
        private readonly int _minWorkerCount;
        private readonly int _maxWorkerCount;
        private readonly int _workerThroughput;
        private readonly Func<Task<int>> _getWorkerCount;
        private readonly TimeSpan _samplingDuration;

        private Timer _idleTimer;
        private int _currentThroughput;

        public ActorThroughputAutoScaleSignaler(
            int minWorkerCount,
            int maxWorkerCount,
            int workerThroughput,
            TimeSpan samplingDuration,
            Func<Task<int>> getWorkerCount)
        {
            _samplingDuration = samplingDuration;
            _workerThroughput = workerThroughput;
            _getWorkerCount = getWorkerCount;
            _maxWorkerCount = maxWorkerCount;
            _minWorkerCount = minWorkerCount;

            MinWorkerCount = _minWorkerCount;
        }

        public event AutoScaleSignaled AutoScaleSignaled;

        public ILogger Logger { get; set; }
        public int MinWorkerCount { get; }

        public void IncrementThroughput()
        {
            Interlocked.Increment(ref _currentThroughput);
        }

        public void Dispose()
        {
            _idleTimer?.Dispose();
        }

        public void Initialize()
        {
            _idleTimer = new Timer(
                SignalAutoScale,
                null,
                TimeSpan.Zero,
                _samplingDuration);
        }

        protected virtual async void SignalAutoScale(object _)
        {
            try
            {
                await AutoScale();
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to auto scale: '{ex.Message}'.", ex);
            }

            async Task AutoScale()
            {
                var currentThroughput = Interlocked.CompareExchange(ref _currentThroughput, 0, 0);
                Interlocked.Exchange(ref _currentThroughput, 0);

                var intendedActorCount = CalculateIntendedWorkerCount(currentThroughput);
                var currentActorCount = await _getWorkerCount();

                var scale = (currentActorCount - intendedActorCount) * -1;
                if (scale != 0)
                {
                    AutoScaleSignaled?.Invoke(scale);
                }
            }

            int CalculateIntendedWorkerCount(int currentThroughput)
            {
                if (_workerThroughput == 0)
                {
                    return _minWorkerCount;
                }

                var count = (int)Math.Ceiling(currentThroughput / (double)_workerThroughput);
                return count switch
                {
                    { } i when i > _maxWorkerCount => _maxWorkerCount,
                    { } i when i < _minWorkerCount => _minWorkerCount,
                    _ => count
                };
            }
        }
    }
}
