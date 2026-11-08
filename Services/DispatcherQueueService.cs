using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace ArcademiaGameLauncher.Services
{
    public class DispatcherQueueService : IDispatcherQueueService
    {
        private readonly ConcurrentQueue<Action> _queue = new();
        private readonly ConcurrentDictionary<string, Action> _uniqueActions = new();
        private readonly DispatcherTimer _timer;
        private readonly ILogger<DispatcherQueueService> _logger;
        private bool _isProcessing = false;
        private const int MaxItemsPerTick = 1;

        public DispatcherQueueService(ILogger<DispatcherQueueService> logger)
        {
            _logger = logger;
            _timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher.UIThread)
            {
                Interval = TimeSpan.FromMilliseconds(10),
            };
            _timer.Tick += ProcessQueue;
            _timer.Start();
        }

        public void Enqueue(Action action)
        {
            _queue.Enqueue(action);
        }

        public void EnqueueUnique(string key, Action action) =>
            _uniqueActions.AddOrUpdate(key, action, (k, existingAction) => action);

        private void ProcessQueue(object sender, EventArgs e)
        {
            if (_isProcessing)
                return;
            _isProcessing = true;

            try
            {
                int itemsProcessed = 0;

                while (itemsProcessed < MaxItemsPerTick && _queue.TryDequeue(out var action))
                {
                    try
                    {
                        action.Invoke();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error executing queued action");
                    }
                    itemsProcessed++;
                }

                if (!_uniqueActions.IsEmpty)
                {
                    var keys = new List<string>(_uniqueActions.Keys);
                    foreach (var key in keys)
                    {
                        if (itemsProcessed >= MaxItemsPerTick * 2)
                            break;

                        if (_uniqueActions.TryRemove(key, out var action))
                        {
                            try
                            {
                                action.Invoke();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error executing unique action: {Key}", key);
                            }

                            itemsProcessed++;
                        }
                    }
                }
            }
            finally
            {
                _isProcessing = false;
            }
        }
    }
}
