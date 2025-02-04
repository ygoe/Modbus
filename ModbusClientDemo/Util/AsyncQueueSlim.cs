namespace ModbusClientDemo.Util;

/// <summary>
/// Represents a first-in, first-out collection of objects with asynchronous and batch access.
/// This class has reduced features from the full AsyncQueue implementation.
/// </summary>
/// <typeparam name="T">Specifies the type of elements in the queue.</typeparam>
public class AsyncQueueSlim<T>
{
	private readonly Queue<T> queue = new();

	/// <summary>
	/// Used to signal the waiting <see cref="DequeueOneAsync"/> method.
	/// Set when new data becomes available. Only reset there.
	/// </summary>
	private TaskCompletionSource dequeueManualTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>
	/// Gets the number of elements contained in the <see cref="AsyncQueueSlim{T}"/>.
	/// </summary>
	public int Count
	{
		get
		{
			lock (queue)
			{
				return queue.Count;
			}
		}
	}

	/// <summary>
	/// Determines whether an element is in the <see cref="AsyncQueueSlim{T}"/>.
	/// </summary>
	/// <param name="predicate">A function to test each item for a condition.</param>
	/// <returns>true if a matching item is found in the queue; otherwise, false.</returns>
	public bool Contains(Predicate<T> predicate)
	{
		lock (queue)
		{
			return queue.Count > 0 && queue.Any(item => predicate(item));
		}
	}

	/// <summary>
	/// Adds an item to the end of the queue.
	/// </summary>
	/// <param name="item">The object to add to the queue.</param>
	public void Enqueue(T item)
	{
		lock (queue)
		{
			queue.Enqueue(item);
			AsyncQueueSlim<T>.Set(dequeueManualTcs);
		}
	}

	/// <summary>
	/// Removes and returns all available objects atomically at the beginning of the queue. The
	/// method completes when at least one object is available.
	/// </summary>
	/// <param name="cancellationToken">A cancellation token used to propagate notification that
	///   this operation should be canceled.</param>
	/// <returns>The objects that are removed from the beginning of the queue.</returns>
	public async Task<T[]> DequeueAvailableAsync(CancellationToken cancellationToken = default)
	{
		while (true)
		{
			TaskCompletionSource myDequeueManualTcs;
			lock (queue)
			{
				if (queue.Count > 0)
				{
					var items = new T[queue.Count];
					for (int i = 0; i < items.Length; i++)
					{
						items[i] = queue.Dequeue();
					}
					return items;
				}
				myDequeueManualTcs = Reset(ref dequeueManualTcs);
			}
			await AwaitAsync(myDequeueManualTcs, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Removes and returns the item at the beginning of the queue. The method completes when at
	/// least one item is available.
	/// </summary>
	/// <param name="cancellationToken">A cancellation token used to propagate notification that
	///   this operation should be canceled.</param>
	/// <returns>The item that is removed from the beginning of the queue.</returns>
	public async Task<T> DequeueOneAsync(CancellationToken cancellationToken = default)
	{
		while (true)
		{
			TaskCompletionSource myDequeueManualTcs;
			lock (queue)
			{
				if (queue.Count > 0)
				{
					return queue.Dequeue();
				}
				myDequeueManualTcs = AsyncQueueSlim<T>.Reset(ref dequeueManualTcs);
			}
			await AsyncQueueSlim<T>.AwaitAsync(myDequeueManualTcs, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Runs a dequeue task that invokes the <paramref name="action"/> for each batch of
	/// asynchronously dequeued items. Process exit will be delayed until all items have been
	/// dequeued and processed.
	/// </summary>
	/// <param name="action">The action to call for each batch of items.</param>
	public async void RunDequeueForever(Action<T[]> action)
	{
		var resetEvent = new ManualResetEvent(false);
		var exitCts = new CancellationTokenSource();
		AppDomain.CurrentDomain.ProcessExit += (s, a) =>
		{
			exitCts.Cancel();
			resetEvent.WaitOne();
		};

		while (!exitCts.IsCancellationRequested)
		{
			try
			{
				var items = await DequeueAvailableAsync(exitCts.Token).ConfigureAwait(false);
				action(items);
			}
			catch (OperationCanceledException)
			{
			}
		}
		resetEvent.Set();
	}

	/// <summary>
	/// Runs a dequeue task that invokes the <paramref name="action"/> for each asynchronously
	/// dequeued item. Process exit will be delayed until all items have been dequeued and processed.
	/// </summary>
	/// <param name="action">The action to call for each item.</param>
	public async void RunDequeueOneForever(Action<T> action)
	{
		var resetEvent = new ManualResetEvent(false);
		var exitCts = new CancellationTokenSource();
		AppDomain.CurrentDomain.ProcessExit += (s, a) =>
		{
			exitCts.Cancel();
			resetEvent.WaitOne();
		};

		while (!exitCts.IsCancellationRequested)
		{
			try
			{
				var item = await DequeueOneAsync(exitCts.Token).ConfigureAwait(false);
				action(item);
			}
			catch (OperationCanceledException)
			{
			}
		}
		resetEvent.Set();
	}

	// Must be called within the lock
	private static TaskCompletionSource Reset(ref TaskCompletionSource tcs)
	{
		if (tcs.Task.IsCompleted)
		{
			tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
		}
		return tcs;
	}

	// Must be called within the lock
	private static void Set(TaskCompletionSource tcs)
	{
		tcs.TrySetResult();
	}

	// Must NOT be called within the lock
	private static async Task AwaitAsync(TaskCompletionSource tcs, CancellationToken cancellationToken)
	{
		if (await Task.WhenAny(tcs.Task, Task.Delay(-1, cancellationToken)).ConfigureAwait(false) == tcs.Task)
		{
			await tcs.Task.ConfigureAwait(false);   // Already completed
			return;
		}
		cancellationToken.ThrowIfCancellationRequested();
	}
}
