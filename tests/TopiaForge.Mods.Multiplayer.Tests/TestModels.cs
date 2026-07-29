using System.Text.Json;
using TopiaForge.Mods;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal sealed class CounterValue
{
    public CounterValue(int value)
    {
        Value = value;
    }

    public int Value { get; }
}

internal sealed class AddRequest
{
    public AddRequest(int amount)
    {
        Amount = amount;
    }

    public int Amount { get; }
}

internal sealed class MutableValue
{
    public MutableValue(int value)
    {
        Value = value;
        History = new List<int> { value };
    }

    public int Value { get; set; }

    public List<int> History { get; set; }
}

internal sealed class TestEvent
{
    public TestEvent(string message)
    {
        Message = message;
    }

    public string Message { get; }
}

internal sealed class JsonTestCodec<T> : IMultiplayerCodec<T> where T : class
{
    public int MaximumEncodedBytes => 4096;

    public OperationResult<byte[]> Encode(T value)
    {
        try
        {
            return OperationResult<byte[]>.Success(JsonSerializer.SerializeToUtf8Bytes(value));
        }
        catch (Exception exception)
        {
            return OperationResult<byte[]>.Failure(ModErrorCode.InvalidArgument, exception.Message);
        }
    }

    public OperationResult<T> Decode(byte[] bytes)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(bytes);
            return value == null
                ? OperationResult<T>.Failure(ModErrorCode.InvalidArgument, "JSON decoded to null.")
                : OperationResult<T>.Success(value);
        }
        catch (Exception exception)
        {
            return OperationResult<T>.Failure(ModErrorCode.InvalidArgument, exception.Message);
        }
    }
}

internal sealed class OversizedTestCodec<T> : IMultiplayerCodec<T> where T : class
{
    public int MaximumEncodedBytes => 1;

    public OperationResult<byte[]> Encode(T value) =>
        OperationResult<byte[]>.Success(new byte[] { 1, 2 });

    public OperationResult<T> Decode(byte[] bytes) =>
        OperationResult<T>.Failure(ModErrorCode.InvalidArgument, "Not used by this test.");
}

internal sealed class ThrowingTestCodec<T> : IMultiplayerCodec<T> where T : class
{
    public int MaximumEncodedBytes => 4096;

    public OperationResult<byte[]> Encode(T value) =>
        throw new InvalidOperationException("Synthetic test codec failure.");

    public OperationResult<T> Decode(byte[] bytes) =>
        throw new InvalidOperationException("Synthetic test codec failure.");
}
