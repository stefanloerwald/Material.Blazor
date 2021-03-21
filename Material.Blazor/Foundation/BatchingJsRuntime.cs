using Microsoft.JSInterop;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;

namespace Material.Blazor.Internal
{
    internal class BatchingJsRuntime : IBatchingJsRuntime
    {
        public interface ICall
        {
            string Identifier { get; }
            object[] Args { get; }
            void SetResult(ref Utf8JsonReader reader);
            void SetExceptionIfNotCompleted(Exception exception);
            void SetException(JSException exception);
        }
        /// <summary>
        /// A javascript call represented by its identifier and arguments
        /// </summary>
        public class Call : ICall
        {
            public string Identifier { get; private set; }
            public object[] Args { get; private set; }
            public Task Task => TaskCompletionSource.Task;
            public TaskCompletionSource TaskCompletionSource { get; private set; }
            public Call(string identifier, object[] args)
            {
                Identifier = identifier;
                Args = args;
                TaskCompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            public void SetResult(ref Utf8JsonReader reader)
            {
                TaskCompletionSource.SetResult();
            }
            public void SetException(JSException exception)
            {
                TaskCompletionSource.SetException(exception);
            }
            public void SetExceptionIfNotCompleted(Exception exception)
            {
                if (!TaskCompletionSource.Task.IsCompleted)
                {
                    TaskCompletionSource.SetException(exception);
                }
            }
        }
        /// <summary>
        /// A javascript call represented by its identifier and arguments
        /// </summary>
        public class Call<T> : ICall
        {
            public string Identifier { get; private set; }
            public object[] Args { get; private set; }
            public Task<T> Task => TaskCompletionSource.Task;
            public TaskCompletionSource<T> TaskCompletionSource { get; private set; }
            public Call(string identifier, object[] args)
            {
                Identifier = identifier;
                Args = args;
                TaskCompletionSource = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            public void SetResult(ref Utf8JsonReader reader)
            {
                try
                {
                    TaskCompletionSource.SetResult(JsonSerializer.Deserialize<T>(ref reader));
                }
                catch (Exception e)
                {
                    TaskCompletionSource.SetException(e);
                }
            }
            public void SetException(JSException exception)
            {
                TaskCompletionSource.SetException(exception);
            }
            public void SetExceptionIfNotCompleted(Exception exception)
            {
                if (!TaskCompletionSource.Task.IsCompleted)
                {
                    TaskCompletionSource.SetException(exception);
                }
            }
        }
        private readonly IJSRuntime js;
        private readonly ConcurrentQueue<ICall> queuedCalls = new ConcurrentQueue<ICall>();
        private readonly Timer timer = new Timer(10);

        public BatchingJsRuntime(IJSRuntime js)
        {
            this.js = js;
            timer.Elapsed += Timer_Elapsed;
        }

        private async void Timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            List<ICall> batch = new();
            while (queuedCalls.TryDequeue(out var call))
            {
                batch.Add(call);
            }
            if (!batch.Any())
            {
                return;
            }
            try
            {
                var result_raw = await js.InvokeAsync<string>("MaterialBlazor.Batching.apply", batch);
                ApplyResults(batch, result_raw);
            }
            catch (Exception ex)
            {
                foreach (var call in batch)
                {
                    call.SetExceptionIfNotCompleted(ex);
                }
            }
        }

        private static void ApplyResults(List<ICall> batch, string result_raw)
        {
            var utf8JsonBytes = Encoding.UTF8.GetBytes(result_raw);
            var reader = new Utf8JsonReader(utf8JsonBytes);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("Invalid JSON");
            }
            foreach (var call in batch)
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                {
                    throw new JsonException("Invalid JSON");
                }
                reader.Read();
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    call.SetResult(ref reader);
                }
                else
                {
                    var value_or_error = reader.GetString();
                    if (value_or_error == "Value")
                    {
                        call.SetResult(ref reader);
                    }
                    else
                    {
                        var error = reader.GetString();
                        call.SetException(new JSException(error));
                    }
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        throw new JsonException("Invalid JSON");
                    }
                }
            }
        }

        /// <summary>
        /// Same as <see cref="JSRuntimeExtensions.InvokeVoidAsync(IJSRuntime, string, object[])"/>, except calls are batched in 20ms intervals.
        /// </summary>
        /// <param name="identifier"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public async Task InvokeVoidAsync(string identifier, params object[] args)
        {
            var call = new Call(identifier, args);
            queuedCalls.Enqueue(call);
            timer.Start();
            await call.Task;
        }
        /// <summary>
        /// Same as <see cref="JSRuntimeExtensions.InvokeAsync{TValue}(IJSRuntime, string, object?[]?)"/>
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="identifier"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public async Task<T> InvokeAsync<T>(string identifier, params object[] args)
        {
            var call = new Call<T>(identifier, args);
            queuedCalls.Enqueue(call);
            timer.Start();
            return await call.Task;
        }
    }
}
