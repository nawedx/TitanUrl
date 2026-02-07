using System.Collections.Concurrent;

namespace TitanUrl.Core.Tests;

public class SnowflakeTests
{
    // Set a fixed epoch for testing
    private readonly DateTimeOffset _epoch = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Generate_ShouldReturnUniqueIds_SingleThread()
    {
        var generator = new SnowflakeIdGenerator(1, _epoch);
        var ids = new HashSet<long>();
        
        // Generate 100,000 IDs
        for (int i = 0; i < 100_000; i++)
        {
            var id = generator.NextId();
            Assert.DoesNotContain(id, ids);
            ids.Add(id);
        }
    }

    [Fact]
    public void Generate_ShouldReturnUniqueIds_MultiThreaded()
    {
        var generator = new SnowflakeIdGenerator(1, _epoch);
        var generatedIds = new ConcurrentBag<long>();
        
        // Simulate high concurrency: 100 tasks generating 1,000 IDs each
        Parallel.For(0, 100, _ =>
        {
            for (int i = 0; i < 1000; i++)
            {
                generatedIds.Add(generator.NextId());
            }
        });

        // Verify total count
        Assert.Equal(100_000, generatedIds.Count);

        // Verify uniqueness
        var uniqueIds = new HashSet<long>(generatedIds);
        Assert.Equal(generatedIds.Count, uniqueIds.Count);
    }

    [Fact]
    public void Generate_ShouldBeMonotonicallyIncreasing()
    {
        var generator = new SnowflakeIdGenerator(1, _epoch);
        long lastId = 0;

        for (int i = 0; i < 10_000; i++)
        {
            long currentId = generator.NextId();
            Assert.True(currentId > lastId, $"ID {currentId} is not greater than {lastId}");
            lastId = currentId;
        }
    }

    [Fact]
    public void Deconstruct_ShouldRetrieveMachineId()
    {
        int machineId = 512; // 1000000000 in binary
        var generator = new SnowflakeIdGenerator(machineId, _epoch);
        
        long id = generator.NextId();

        // Extract Machine ID: Shift right by 12 (Sequence), then mask with 10 bits (1023)
        long extractedMachineId = (id >> 12) & 1023;

        Assert.Equal(machineId, extractedMachineId);
    }
}