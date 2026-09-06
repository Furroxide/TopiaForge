using System;
using System.Threading.Tasks;

namespace TopiaForge.ModManager
{
    internal interface IHostDispatcher
    {
        bool IsCurrent { get; }
        void Post(Action action);
        Task InvokeAsync(Action action);
        Task<T> InvokeAsync<T>(Func<T> action);
        Task<T> InvokeCallbackAsync<T>(Func<Task<T>> callback);
    }
}
