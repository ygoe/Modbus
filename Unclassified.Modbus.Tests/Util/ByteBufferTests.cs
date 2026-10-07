using Unclassified.Modbus.Util;

namespace Unclassified.Modbus.Tests.Util;

/// <summary>
/// Tests the <see cref="ByteBuffer"/> class.
/// </summary>
[TestClass]
public class ByteBufferTests
{
	#region Private data

	/// <summary>
	/// The maximum time to wait for an asynchronous operation that is expected to complete.
	/// </summary>
	private static readonly TimeSpan completionTimeout = TimeSpan.FromSeconds(5);

	#endregion Private data

	#region Synchronous methods

	/// <summary>
	/// Checks that bytes are dequeued in the order they were enqueued.
	/// </summary>
	[TestMethod]
	public void Dequeue_FirstInFirstOut()
	{
		var buffer = new ByteBuffer();
		buffer.Enqueue([1, 2, 3]);
		buffer.Enqueue([4, 5]);

		Assert.AreEqual(5, buffer.Count);
		CollectionAssert.AreEqual(new byte[] { 1, 2 }, buffer.Dequeue(2));
		CollectionAssert.AreEqual(new byte[] { 3, 4, 5 }, buffer.Dequeue(10));
		Assert.AreEqual(0, buffer.Count);
	}

	/// <summary>
	/// Checks that data wrapping around the end of the internal buffer stays in order.
	/// </summary>
	[TestMethod]
	public void Enqueue_WrapsAround()
	{
		var buffer = new ByteBuffer(4);
		buffer.Enqueue([1, 2, 3]);
		buffer.Dequeue(2);
		buffer.Enqueue([4, 5, 6]);

		Assert.AreEqual(4, buffer.Capacity);
		CollectionAssert.AreEqual(new byte[] { 3, 4, 5, 6 }, buffer.Buffer);
		CollectionAssert.AreEqual(new byte[] { 3, 4, 5, 6 }, buffer.Dequeue(4));
	}

	/// <summary>
	/// Checks that the buffer grows when more data is enqueued than it can hold.
	/// </summary>
	[TestMethod]
	public void Enqueue_GrowsCapacity()
	{
		var buffer = new ByteBuffer(4);
		buffer.Enqueue([1, 2, 3]);
		buffer.Dequeue(1);
		buffer.Enqueue([4, 5, 6, 7, 8]);

		Assert.IsGreaterThanOrEqualTo(7, buffer.Capacity);
		CollectionAssert.AreEqual(new byte[] { 2, 3, 4, 5, 6, 7, 8 }, buffer.Dequeue(7));
	}

	/// <summary>
	/// Checks that peeking does not remove the bytes.
	/// </summary>
	[TestMethod]
	public void Peek_KeepsBytes()
	{
		var buffer = new ByteBuffer();
		buffer.Enqueue([1, 2, 3]);

		CollectionAssert.AreEqual(new byte[] { 1, 2 }, buffer.Peek(2));
		Assert.AreEqual(3, buffer.Count);
	}

	/// <summary>
	/// Checks that the buffer is empty after clearing it.
	/// </summary>
	[TestMethod]
	public void Clear_RemovesAllBytes()
	{
		var buffer = new ByteBuffer();
		buffer.Enqueue([1, 2, 3]);
		buffer.Clear();

		Assert.AreEqual(0, buffer.Count);
		Assert.IsEmpty(buffer.Dequeue(1));
	}

	#endregion Synchronous methods

	#region Asynchronous methods

	/// <summary>
	/// Checks that dequeuing into a new array waits until enough bytes are available.
	/// </summary>
	[TestMethod]
	public async Task DequeueAsync_WaitsForCount()
	{
		var buffer = new ByteBuffer();
		buffer.Enqueue([1]);
		var task = buffer.DequeueAsync(3);
		Assert.IsFalse(task.IsCompleted);

		buffer.Enqueue([2, 3, 4]);

		CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await task.WaitAsync(completionTimeout));
		Assert.AreEqual(1, buffer.Count);
	}

	/// <summary>
	/// Checks that dequeuing into an existing array completes once enough bytes are available.
	/// </summary>
	[TestMethod]
	public async Task DequeueAsyncIntoBuffer_WaitsForCount()
	{
		var buffer = new ByteBuffer();
		byte[] data = new byte[5];
		var task = buffer.DequeueAsync(data, 1, 3);
		Assert.IsFalse(task.IsCompleted);

		buffer.Enqueue([1, 2, 3, 4]);

		await task.WaitAsync(completionTimeout);
		CollectionAssert.AreEqual(new byte[] { 0, 1, 2, 3, 0 }, data);
		Assert.AreEqual(1, buffer.Count);
	}

	/// <summary>
	/// Checks that dequeuing into an existing array completes immediately if enough bytes are
	/// available and dequeues them only once.
	/// </summary>
	[TestMethod]
	public async Task DequeueAsyncIntoBuffer_DataAvailable_DequeuesOnce()
	{
		var buffer = new ByteBuffer();
		buffer.Enqueue([1, 2, 3, 4, 5, 6]);
		byte[] data = new byte[2];

		await buffer.DequeueAsync(data, 0, 2).WaitAsync(completionTimeout);

		CollectionAssert.AreEqual(new byte[] { 1, 2 }, data);
		Assert.AreEqual(4, buffer.Count);
	}

	/// <summary>
	/// Checks that a waiting dequeue operation can be cancelled.
	/// </summary>
	[TestMethod]
	public async Task DequeueAsync_Cancelled_Throws()
	{
		var buffer = new ByteBuffer();
		using var cts = new CancellationTokenSource();
		var task = buffer.DequeueAsync(1, cts.Token);

		cts.Cancel();

		await Assert.ThrowsAsync<OperationCanceledException>(
			() => task.WaitAsync(completionTimeout));
	}

	/// <summary>
	/// Checks that waiting for data completes when data is enqueued.
	/// </summary>
	[TestMethod]
	public async Task WaitAsync_CompletesOnEnqueue()
	{
		var buffer = new ByteBuffer();
		var task = buffer.WaitAsync();
		Assert.IsFalse(task.IsCompleted);

		buffer.Enqueue([1]);

		await task.WaitAsync(completionTimeout);
		Assert.AreEqual(1, buffer.Count);
	}

	#endregion Asynchronous methods
}
