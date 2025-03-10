using System.Runtime.CompilerServices;

namespace Unclassified.Modbus.Util;

/// <summary>
/// Provides common helper and extension methods for tasks and other asynchronous code.
/// </summary>
internal static class TaskHelper
{
	/// <summary>
	/// Creates a new <see cref="CancellationTokenSource"/> that observes the provided
	/// <paramref name="cancellationToken"/> but also cancels after the <paramref name="timeout"/>.
	/// </summary>
	/// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
	/// <param name="timeout">The timeout of the operation.</param>
	/// <returns>
	/// A new <see cref="CancellationTokenSource"/> instance that provides a new
	/// <see cref="CancellationToken"/> to observe. This instance must be disposed by the caller.
	/// </returns>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068:CancellationToken parameters must come last", Justification = "CancellationToken is enhanced by timeout")]
	public static CancellationTokenSource CreateTimeoutCancellationTokenSource(CancellationToken cancellationToken, TimeSpan timeout)
	{
		var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		linkedCts.CancelAfter(timeout);
		return linkedCts;
	}

	/// <summary>
	/// Creates a new exception to throw instead of a caught <see cref="OperationCanceledException"/>
	/// after using a new <see cref="CancellationToken"/> from the
	/// <see cref="CreateTimeoutCancellationTokenSource"/> method.
	/// </summary>
	/// <param name="ex">The caught <see cref="OperationCanceledException"/>.</param>
	/// <param name="cancellationToken">The <see cref="CancellationToken"/> provided by the caller.</param>
	/// <returns>
	/// A new <see cref="OperationCanceledException"/> that contains the provided
	/// <paramref name="cancellationToken"/>, if it was cancelled; otherwise, a
	/// <see cref="TimeoutException"/> that better indicates that the timeout has expired.
	/// </returns>
	public static Exception GetOperationCanceledOrTimeoutException(OperationCanceledException ex, CancellationToken cancellationToken)
	{
		if (cancellationToken.IsCancellationRequested)
		{
			// Rewrite the exception to include the provided CancellationToken, preserving
			// the full stack trace to the cancelled operation in the inner exception
			return new OperationCanceledException(ex.Message, ex, cancellationToken);
		}
		else
		{
			// Throw a specialised exception
			return new TimeoutException("The operation has timed out.", ex);
		}
	}

	/// <summary>
	/// Configures an awaiter to not attempt to marshal the continuation back to the original
	/// synchronization context captured. This is a shortcut to <c>ConfigureAwait(false)</c> which
	/// should be used in all general-purpose library code by default.
	/// </summary>
	/// <param name="task">The task to configure the awaiter of.</param>
	/// <returns>A configured Task awaitable.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static ConfiguredTaskAwaitable NoSync(this Task task)
	{
		return task.ConfigureAwait(false);
	}

	/// <summary>
	/// Configures an awaiter to not attempt to marshal the continuation back to the original
	/// synchronization context captured. This is a shortcut to <c>ConfigureAwait(false)</c> which
	/// should be used in all general-purpose library code by default.
	/// </summary>
	/// <typeparam name="TResult">The type of the result produced by the <paramref name="task"/>.</typeparam>
	/// <param name="task">The task to configure the awaiter of.</param>
	/// <returns>A configured Task awaitable.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static ConfiguredTaskAwaitable<TResult> NoSync<TResult>(this Task<TResult> task)
	{
		return task.ConfigureAwait(false);
	}

	/// <summary>
	/// Configures an awaiter to not attempt to marshal the continuation back to the original
	/// synchronization context captured. This is a shortcut to <c>ConfigureAwait(false)</c> which
	/// should be used in all general-purpose library code by default.
	/// </summary>
	/// <param name="task">The task to configure the awaiter of.</param>
	/// <returns>A configured Task awaitable.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static ConfiguredValueTaskAwaitable NoSync(this ValueTask task)
	{
		return task.ConfigureAwait(false);
	}

	/// <summary>
	/// Configures an awaiter to not attempt to marshal the continuation back to the original
	/// synchronization context captured. This is a shortcut to <c>ConfigureAwait(false)</c> which
	/// should be used in all general-purpose library code by default.
	/// </summary>
	/// <typeparam name="TResult">The type of the result produced by the <paramref name="task"/>.</typeparam>
	/// <param name="task">The task to configure the awaiter of.</param>
	/// <returns>A configured Task awaitable.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static ConfiguredValueTaskAwaitable<TResult> NoSync<TResult>(this ValueTask<TResult> task)
	{
		return task.ConfigureAwait(false);
	}

	/// <summary>
	/// Awaits a task and ignores a thrown <see cref="OperationCanceledException"/>. All other
	/// exceptions are passed through.
	/// </summary>
	/// <param name="task">The task to await.</param>
	/// <returns>A task that resolves when the specified <paramref name="task"/> resolves.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static async Task IgnoreCanceled(this Task task)
	{
		try
		{
			await task.NoSync();
		}
		catch (OperationCanceledException) when (task.IsCanceled)
		{
		}
	}

	/// <summary>
	/// Awaits a task and ignores a thrown <see cref="OperationCanceledException"/>. All other
	/// exceptions are passed through. The value of the <paramref name="task"/> is passed through.
	/// If the task was cancelled, a default value is returned.
	/// </summary>
	/// <param name="task">The task to await.</param>
	/// <returns>A task that resolves when the specified <paramref name="task"/> resolves.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static async Task<T?> IgnoreCanceled<T>(this Task<T> task)
	{
		try
		{
			return await task.NoSync();
		}
		catch (OperationCanceledException) when (task.IsCanceled)
		{
			return default;
		}
	}

	/// <summary>
	/// Asynchronously waits until a cancellation has been requested.
	/// </summary>
	/// <param name="cancellationToken">The cancellation token to wait for.</param>
	/// <returns>The task object representing the asynchronous operation.</returns>
	public static async Task WaitAsync(this CancellationToken cancellationToken)
	{
		var tcs = new TaskCompletionSource();
		using (cancellationToken.Register(() => tcs.TrySetResult(), useSynchronizationContext: false))
		{
			await tcs.Task.NoSync();
		}
	}
}
