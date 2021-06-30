using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Villain
{
    public abstract class ActorBatchConsumer<T> : Actor<T>
    {
        private readonly List<T> _buffer;

        protected ActorBatchConsumer(ChannelReader<T> channel) : base(channel)
        {
            _buffer = new List<T>();
        }

        public override async ValueTask Dispose(CancellationToken token)
        {
            await OnInboxNotAvailable(true, token);
            await base.Dispose(token);
        }

        protected sealed override Task OnInboxReceived(T data, CancellationToken token)
        {
            _buffer.Add(data);

            if (_buffer.Count < BatchSize && BatchSize > 1)
            {
                return Task.CompletedTask;
            }

            var messagesToWrite = new List<T>(_buffer);
            _buffer.Clear();

            return OnBatchDispatched(messagesToWrite, false, token);
        }

        protected sealed override Task OnInboxTimeout(CancellationToken token) => 
            OnInboxNotAvailable(false, token);

        protected sealed override Task OnInboxClosed(CancellationToken token) => 
            OnInboxNotAvailable(false, token);

        protected sealed override TimeSpan ReadTimeout =>
            _buffer.Any() ? ReadTimeoutOnActiveBuffer : TimeSpan.FromSeconds(30);

        protected abstract Task OnBatchDispatched(IEnumerable<T> batch, bool isFinalFlush, CancellationToken token);
        protected abstract int BatchSize { get; }
        protected abstract TimeSpan ReadTimeoutOnActiveBuffer { get; }

        private Task OnInboxNotAvailable(
            bool isFinalFlush,
            CancellationToken token)
        {
            var messagesToWrite = new List<T>(_buffer);
            _buffer.Clear();

            return OnBatchDispatched(messagesToWrite, isFinalFlush, token);
        }
    }
}
