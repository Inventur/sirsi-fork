using CO.CDP.MQ.Outbox;
using CO.CDP.Testcontainers.PostgreSql;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Npgsql;
using Range = Moq.Range;

namespace CO.CDP.MQ.Tests.Outbox;

public class OutboxProcessorListenerTest(PostgreSqlFixture postgreSql) : IClassFixture<PostgreSqlFixture>
{
    private readonly TestDbContext _dbContext = postgreSql.TestDbContext();
    private readonly CancellationTokenSource _tokenSource = new();
    private readonly Mock<IOutboxProcessor> _processor = new();
    private readonly ILogger<OutboxProcessorListener> _logger =
        LoggerFactory.Create(_ => { }).CreateLogger<OutboxProcessorListener>();
    private int _messagesToBeProcessedCount;
    private readonly SemaphoreSlim _processingComplete = new(0);

    [Fact]
    public async Task ItInvokesTheOutboxProcessorOnEveryNotification()
    {
        GivenOutboxProcessorPublishesMessagesWithNoInterruptions();

        var dataSource = GetDataSource();
        var listener = new OutboxProcessorListener(dataSource, _processor.Object, _logger);

        _ = listener.WaitAsync(10, _tokenSource.Token);

        await GivenOutboxMessagesAreCreated([
            OutboxMessage(message: "Hello!", published: false),
            OutboxMessage(message: "Hello, again!", published: false),
        ]);

        await WaitForProcessingComplete(TimeSpan.FromSeconds(30));
        await _tokenSource.CancelAsync();

        // We pull up to 10 messages at a time and expect 3 calls:
        // * 1 before we start listening to notifications
        // * 2 notifications
        _processor.Verify(p => p.ExecuteAsync(10), Times.Between(1, 3, Range.Inclusive));
        _messagesToBeProcessedCount.Should().Be(0);
    }

    [Fact]
    public async Task ItPullsMessagesUntilThePullIsExhaustedBeforeSwitchingToListeningMode()
    {
        GivenOutboxProcessorPublishesMessagesWithNoInterruptions();

        var dataSource = GetDataSource();
        var listener = new OutboxProcessorListener(dataSource, _processor.Object, _logger);

        await GivenOutboxMessagesAreCreated([
            OutboxMessage(message: "Hello!", published: false),
            OutboxMessage(message: "Hello, again!", published: false),
        ]);

        _ = listener.WaitAsync(1, _tokenSource.Token);

        await WaitForProcessingComplete(TimeSpan.FromSeconds(30));

        await GivenOutboxMessagesAreCreated([
            OutboxMessage(message: "Bye!", published: false),
            OutboxMessage(message: "Bye, again!", published: false),
        ]);

        await WaitForProcessingComplete(TimeSpan.FromSeconds(30));
        await _tokenSource.CancelAsync();

        // We pull up to 1 message at a time and expect 7 calls:
        // * 3 before we start listening to notifications (3rd pulls 0)
        // * 4 notifications (twice for each message, every other one pulls 0)
        _processor.Verify(p => p.ExecuteAsync(1), Times.Between(6, 7, Range.Inclusive));
        _messagesToBeProcessedCount.Should().Be(0);
    }

    private void GivenOutboxProcessorPublishesMessagesWithNoInterruptions()
    {
        // There's a relationship between the number of messages persisted via the OutboxMessageRepository
        // and the number of messages possible to be processed by the OutboxProcessor.
        // It is capture in the ReturnsAsync code below.
        _processor.Setup(p => p.ExecuteAsync(It.IsAny<int>()))
            .ReturnsAsync((int batchSize) =>
            {
                var processed = batchSize >= _messagesToBeProcessedCount ? _messagesToBeProcessedCount : batchSize;
                _messagesToBeProcessedCount -= processed;
                if (_messagesToBeProcessedCount <= 0)
                {
                    _processingComplete.Release();
                }
                return processed;
            });
    }

    private async Task WaitForProcessingComplete(TimeSpan timeout)
    {
        var acquired = await _processingComplete.WaitAsync(timeout);
        if (!acquired)
        {
            throw new TimeoutException(
                $"Messages were not processed within {timeout.TotalSeconds}s. " +
                $"Remaining: {_messagesToBeProcessedCount}");
        }
    }

    private static OutboxMessage OutboxMessage(
        string type = "Greeting",
        string message = "Hello.",
        bool published = false
    )
    {
        return new OutboxMessage()
        {
            Type = type,
            Message = message,
            Published = published,
            QueueUrl = "test-queue",
            MessageGroupId = "test-messages"
        };
    }

    private async Task GivenOutboxMessagesAreCreated(List<OutboxMessage> messages)
    {
        var repository = CreateDatabaseOutboxMessageRepository(_dbContext);
        foreach (var message in messages)
        {
            _messagesToBeProcessedCount += 1;
            await repository.SaveAsync(message);
        }
    }

    private NpgsqlDataSource GetDataSource()
    {
        return new NpgsqlDataSourceBuilder(_dbContext.Database.GetConnectionString()).Build();
    }

    private DatabaseOutboxMessageRepository<TestDbContext> CreateDatabaseOutboxMessageRepository(
        TestDbContext dbContext)
    {
        return new DatabaseOutboxMessageRepository<TestDbContext>(dbContext);
    }
}
