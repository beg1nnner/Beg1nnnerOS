using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Beg1nnnerOS.Core;

/// <summary>
/// Однопоточный цикл событий (аналог asyncio в Python-версии): вся логика приложения
/// выполняется в одном потоке, поэтому общие данные не требуют блокировок.
/// </summary>
public sealed class HubLoop : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Cb, object State)> _queue = new();
    private readonly Thread _thread;
    public Action<Exception> OnError;

    public HubLoop()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "hub-loop" };
        _thread.Start();
    }

    public bool IsLoopThread => Thread.CurrentThread == _thread;

    private void Run()
    {
        SetSynchronizationContext(this);
        foreach (var (cb, state) in _queue.GetConsumingEnumerable())
        {
            try { cb(state); }
            catch (Exception e) { OnError?.Invoke(e); }
        }
    }

    public override void Post(SendOrPostCallback d, object state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object state)
    {
        if (IsLoopThread) { d(state); return; }
        using var done = new ManualResetEventSlim();
        Exception error = null;
        Post(_ =>
        {
            try { d(state); }
            catch (Exception e) { error = e; }
            finally { done.Set(); }
        }, null);
        done.Wait();
        if (error != null) throw error;
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Выполнить действие в цикле (из любого потока).</summary>
    public void Post(Action action) => Post(_ => action(), null);

    /// <summary>Запустить фоновую задачу в цикле; исключения попадают в OnError.</summary>
    public void Start(Func<Task> work)
    {
        Post(async _ =>
        {
            try { await work(); }
            catch (OperationCanceledException) { }
            catch (Exception e) { OnError?.Invoke(e); }
        }, null);
    }

    /// <summary>Выполнить асинхронную функцию в цикле и вернуть результат вызывающему потоку.</summary>
    public Task<T> Call<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try { tcs.SetResult(await work()); }
            catch (Exception e) { tcs.SetException(e); }
        }, null);
        return tcs.Task;
    }

    public Task Run(Func<Task> work)
    {
        return Call<bool>(async () => { await work(); return true; });
    }

    public Task<T> CallSync<T>(Func<T> work)
    {
        return Call<T>(() => Task.FromResult(work()));
    }
}
