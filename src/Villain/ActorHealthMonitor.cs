using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Villain
{
    public interface IActorHealthMonitorStrategy
    {
    }

    public class RollingActorHealthMonitorStrategy : IActorHealthMonitorStrategy
    {
        public int MinimumThroughputSecs { get; set; }
    }

    public interface IActorHealthMonitor
    {
        double GetFailureRate();
    }

    public class NullActorHealthMonitor : IActorHealthMonitor
    {
        public double GetFailureRate() => 0;
    }

    public class RollingActorHealthMonitor : IActorHealthMonitor, IDisposable
    {
        private const int NumberOfWindows = 10;

        private readonly long _samplingDuration;
        private readonly long _windowDuration;

        private readonly ReaderWriterLock _lock;
        private readonly Queue<HealthCount> _windows;
        private readonly IHealthEventsRegistrar _healthEventsRegistrar;
        private readonly RollingActorHealthMonitorStrategy _strategy;

        private HealthCount _currentWindow;

        public RollingActorHealthMonitor(
            RollingActorHealthMonitorStrategy strategy,
            IHealthEventsRegistrar healthEventsRegistrar)
        {
            _strategy = strategy;
            _healthEventsRegistrar = healthEventsRegistrar;

            var samplingDuration = TimeSpan.FromSeconds(30);
            

            _samplingDuration = samplingDuration.Ticks;

            _windowDuration = _samplingDuration / NumberOfWindows;
            _windows = new Queue<HealthCount>(NumberOfWindows + 1);

            _healthEventsRegistrar.OnSuccess += OnHealthEventsRegistrarOnOnSuccess;
            _healthEventsRegistrar.OnFailure += OnHealthEventsRegistrarOnOnFailure;

            _lock = new ReaderWriterLock();
        }

        protected virtual bool ShouldHandleEvent(object sender, EventArgs eventArgs) => true;

        private void OnHealthEventsRegistrarOnOnSuccess(object sender, EventArgs eventArgs)
        {
            if (ShouldHandleEvent(sender, eventArgs))
            {
                IncrementSuccess();
            }
        }

        private void OnHealthEventsRegistrarOnOnFailure(object sender, EventArgs eventArgs)
        {
            if (ShouldHandleEvent(sender, eventArgs))
            {
                IncrementFailure();
            }
        }

        public double GetFailureRate()
        {
            WithWriterLock(ActualizeCurrentMetric);

            return WithReaderLock(CalculateFailureRate);

            double CalculateFailureRate()
            {
                var successes = 0;
                var failures = 0;
                foreach (var window in _windows)
                {
                    successes += window.Successes;
                    failures += window.Failures;
                }

                var throughput = successes + failures;
                if (throughput < _strategy.MinimumThroughputSecs)
                {
                    return 0;
                }

                return (double)failures / throughput;
            }
        }

        private void IncrementSuccess()
        {
            WithWriterLock(IncreaseCurrentWindowSuccess);

            void IncreaseCurrentWindowSuccess()
            {
                ActualizeCurrentMetric();
                _currentWindow.Successes++;
            }
        }

        private void IncrementFailure()
        {
            WithWriterLock(IncreaseCurrentWindowFailure);

            void IncreaseCurrentWindowFailure()
            {
                ActualizeCurrentMetric();
                _currentWindow.Failures++;
            }
        }

        private void ActualizeCurrentMetric()
        {
            var now = DateTime.UtcNow.Ticks;
            if (_currentWindow == null || now - _currentWindow.StartedAt >= _windowDuration)
            {
                _currentWindow = new HealthCount {StartedAt = now};
                _windows.Enqueue(_currentWindow);
            }

            while (_windows.Count > 0 && (now - _windows.Peek().StartedAt >= _samplingDuration))
            {
                _windows.Dequeue();
            }
        }

        private void WithWriterLock(Action work)
        {
            try
            {
                _lock.AcquireWriterLock(500);
                work();
            }
            finally
            {
                _lock.ReleaseWriterLock();
            }
        }

        private TResult WithReaderLock<TResult>(Func<TResult> work)
        {
            try
            {
                _lock.AcquireReaderLock(500);
                return work();
            }
            finally
            {
                _lock.ReleaseReaderLock();
            }
        }

        private class HealthCount
        {
            public int Successes { get; set; }
            public int Failures { get; set; }
            public long StartedAt { get; set; }
        }

        public virtual void Dispose()
        {
            _windows.Clear();
            _healthEventsRegistrar.OnSuccess -= OnHealthEventsRegistrarOnOnSuccess;
            _healthEventsRegistrar.OnFailure -= OnHealthEventsRegistrarOnOnFailure;
        }
    }
}
