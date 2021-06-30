# Villain

An in-process Actor framework.

Work in progress...

## Example

Send messages via an actor dispatcher to be consumed by an actor(s)...

```cs
public class MessageSender : IMessageSender
{
    private readonly IActorPublisher<string> _dispatcher;

    public MessageSender(IActorPublisher<string> dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public Task<bool> Send(string message) => 
        _dispatcher.Enqueue(message);

    public Task<(IEnumerable<string> sent, IEnumerable<string> notSent)> Send(IEnumerable<string> batch) =>
        _dispatcher.EnqueueBatch(batch);
}
```

Configure an actor dispatcher.

```cs
public class MessageWriterDispatcher : ActorBalancedDispatcher<string>
{
    public MessageWriterDispatcher(IActorFactory<string> actorFactory) : base(actorFactory)
    {
    }

    protected override Channel<string> ChannelFactory => 
        System.Threading.Channels.Channel.CreateBounded<string>(
            new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.Wait
            });

    protected override IAutoScaleSignaler AutoScaleSignalerFactory => 
        new ActorThroughputAutoScaleSignaler(
            minWorkerCount: 1, 
            maxWorkerCount: 5, 
            workerThroughput: 20, 
            samplingDuration: TimeSpan.FromSeconds(30), 
            getWorkerCount: GetActorCount);
}
```

Configure a consuming actor.

```cs
public class MessageWriterActor : Actor<string>
{
    public MessageWriterActor(ChannelReader<string> channel) : base(channel)
    {
    }

    protected override Task OnInboxReceived(string data, CancellationToken token)
    {
        Console.WriteLine($"Received message: {data}.");
        return Task.CompletedTask;
    }

    protected override Task OnError(Exception e, CancellationToken token)
    {
        Console.WriteLine($"Received error: '{e.Message}'.");
        return Task.CompletedTask;
    }

    protected override Task OnInboxClosed(CancellationToken token)
    {
        Console.WriteLine("Inbox closed.");
        return Task.CompletedTask;
    }
}
```

Or... Configure a batch consuming actor.

```cs
public class BatchedMessageWriter : ActorBatchConsumer<string>
{
    public BatchedMessageWriter(ChannelReader<string> channel) : base(channel)
    {
    }

    protected override Task OnBatchDispatched(
        IEnumerable<string> batch, bool isFinalFlush, CancellationToken token)
    {
        Console.WriteLine($"Received {batch.Count()} messages.");
        return Task.CompletedTask;
    }

    protected override Task OnError(Exception e, CancellationToken token)
    {
        Console.WriteLine($"Received error: '{e.Message}'.");
        return Task.CompletedTask;
    }

    protected override int BatchSize => 50;
    protected override TimeSpan ReadTimeoutOnActiveBuffer => TimeSpan.FromSeconds(30);
}
```