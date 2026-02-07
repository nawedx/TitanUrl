namespace TitanUrl.Core;

public class SnowflakeIdGenerator
{
    // Configuration Constants
    private const int MachineIdBits = 10;
    private const int SequenceBits = 12;
    
    // Max Values (Calculated via bit shifting)
    private const int MaxMachineId = -1 ^ (-1 << MachineIdBits); // 1023
    private const int MaxSequence = -1 ^ (-1 << SequenceBits);   // 4095

    // Bit Shifts
    private const int MachineIdShift = SequenceBits;
    private const int TimestampShift = SequenceBits + MachineIdBits;

    // State
    private readonly int _machineId;
    private readonly long _epoch;
    private long _lastTimestamp = -1L;
    private int _sequence;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes the generator.
    /// </summary>
    /// <param name="machineId">Unique ID for this specific server/process (0-1023).</param>
    /// <param name="customEpoch">The start date of your system (e.g., today).</param>
    public SnowflakeIdGenerator(int machineId, DateTimeOffset customEpoch)
    {
        if (machineId is < 0 or > MaxMachineId)
        {
            throw new ArgumentException($"Machine ID must be between 0 and {MaxMachineId}");
        }

        _machineId = machineId;
        _epoch = customEpoch.ToUnixTimeMilliseconds();
    }

    public long NextId()
    {
        lock (_lock)
        {
            var timestamp = CurrentTimeMillis();

            if (timestamp < _lastTimestamp)
            {
                throw new Exception($"Clock moved backwards. Refusing to generate id for {_lastTimestamp - timestamp} milliseconds");
            }

            if (_lastTimestamp == timestamp)
            {
                // Same millisecond: increment sequence
                _sequence = (_sequence + 1) & MaxSequence;
                
                if (_sequence == 0)
                {
                    // Sequence overflow: wait for next millisecond
                    timestamp = WaitNextMillis(_lastTimestamp);
                }
            }
            else
            {
                // New millisecond: reset sequence
                _sequence = 0;
            }

            _lastTimestamp = timestamp;

            // Construct the 64-bit ID
            return ((timestamp - _epoch) << TimestampShift) |
                   ((long)_machineId << MachineIdShift) |
                   (uint)_sequence;
        }
    }

    private long WaitNextMillis(long lastTimestamp)
    {
        var timestamp = CurrentTimeMillis();
        while (timestamp <= lastTimestamp)
        {
            timestamp = CurrentTimeMillis();
        }
        return timestamp;
    }

    private long CurrentTimeMillis()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}