using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiThemePackage
    {
        internal static readonly OperationResult<bool> Success = OperationResult<bool>.Success(true);
        private static readonly OperationResult<bool> Cancelled =
            OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The theme request ended before registration completed.");
        private static readonly OperationResult<bool> NotDeclared =
            OperationResult<bool>.Failure(ModErrorCode.NotFound, "The theme is not declared in this package.");
        private static readonly Task<OperationResult<bool>> CancelledTask = Task.FromResult(Cancelled);
        private static readonly Task<OperationResult<bool>> ReadyTask = Task.FromResult(Success);

        private readonly object gate = new object();
        private readonly Dictionary<string, FakeUiThemeRegistration> registrations =
            new Dictionary<string, FakeUiThemeRegistration>(StringComparer.Ordinal);
        private readonly List<FakeUiThemeScope> scopes = new List<FakeUiThemeScope>();
        private readonly HashSet<FakeUiThemeRegistration> demanded = new HashSet<FakeUiThemeRegistration>();
        private string? manifestDefault;
        private bool fixturesFrozen;

        internal void Attach(FakeUiThemeScope scope)
        {
            lock (gate)
            {
                scope.Stopped = scope.Parent?.Stopped == true;
                if (!scope.Stopped) scopes.Add(scope);
            }
        }

        internal UiThemeRef? Selection(FakeUiThemeScope scope)
        {
            lock (gate) return scope.Selection;
        }

        internal OperationResult<bool> Select(FakeUiThemeScope scope, UiThemeRef? theme)
        {
            lock (gate)
            {
                var admitted = ValidateSelection(scope, theme);
                if (!admitted.Succeeded) return admitted;
                scope.Selection = theme;
                Reconcile(theme?.Id);
                return Success;
            }
        }

        internal OperationResult<bool> ValidateSelection(FakeUiThemeScope scope, UiThemeRef? theme)
        {
            lock (gate)
            {
                if (scope.Stopped) return Cancelled;
                return theme == null || registrations.ContainsKey(theme.Id) ? Success : NotDeclared;
            }
        }

        internal void AddSurface(FakeUiThemeScope scope, FakeUiSurface surface)
        {
            lock (gate)
            {
                if (scope.Stopped) return;
                scope.Surfaces.Add(surface);
                Reconcile(surface.Request.Theme?.Id);
            }
        }

        internal void RemoveSurface(FakeUiThemeScope scope, FakeUiSurface surface)
        {
            lock (gate)
            {
                if (scope.Surfaces.Remove(surface)) Reconcile();
            }
        }

        internal FakeUiThemeResolution Resolve(FakeUiThemeScope scope, UiThemeRef? surfaceTheme)
        {
            lock (gate)
            {
                var id = EffectiveId(scope, surfaceTheme);
                if (id != null && registrations.TryGetValue(id, out var registration) && registration.Loaded != null)
                    return new FakeUiThemeResolution(registration.Loaded, id, usedFallback: false);
                return new FakeUiThemeResolution(UiThemePackParser.Default, id, usedFallback: id != null);
            }
        }

        internal UiThemeDescriptor? DescriptorForWire(FakeUiThemeScope scope, UiThemeRef? surfaceTheme)
        {
            lock (gate)
            {
                var id = EffectiveId(scope, surfaceTheme);
                return id != null && registrations.TryGetValue(id, out var registration)
                    ? registration.Loaded ?? registration.Definition.Descriptor
                    : null;
            }
        }

        internal Task<OperationResult<bool>> Preload(FakeUiThemeScope scope, UiThemeRef theme, CancellationToken cancellation)
        {
            lock (gate)
            {
                if (scope.Stopped || cancellation.IsCancellationRequested) return CancelledTask;
                if (!registrations.TryGetValue(theme.Id, out var registration)) return Task.FromResult(NotDeclared);

                if (!scope.Preloads.TryGetValue(theme.Id, out var preload))
                    scope.Preloads.Add(theme.Id, preload = new FakeUiThemePreload());

                if (registration.Loaded != null)
                {
                    preload.Retained = true;
                    return ReadyTask;
                }

                var waiter = new FakeUiThemeWaiter();
                preload.Waiters.Add(waiter);
                waiter.Cancellation = cancellation.Register(() => Cancel(scope, theme.Id, waiter));
                // An already-cancelled token invokes its callback before returning the registration.
                if (waiter.Completion.Task.IsCompleted) _ = waiter.Cancellation.DisposeAsync();
                Reconcile(theme.Id);
                return waiter.Completion.Task;
            }
        }

        internal void Advance()
        {
            lock (gate)
            {
                foreach (var registration in registrations.Values)
                {
                    if (registration.State == FakeUiThemeLoadState.Draining)
                    {
                        registration.State = FakeUiThemeLoadState.Idle;
                        continue;
                    }

                    if (registration.State != FakeUiThemeLoadState.Loading) continue;
                    var result = Acquire(registration.Definition);
                    registration.Loaded = result.Value;
                    registration.State = result.Succeeded ? FakeUiThemeLoadState.Ready : FakeUiThemeLoadState.Failed;
                    FinishWaiters(registration.Definition.Descriptor.Id, result.Succeeded
                        ? Success
                        : OperationResult<bool>.Failure(result.ErrorCode, result.ErrorMessage));
                }

                // Reconcile only after the whole batch. An attempt made eligible by a drain does not complete this step.
                Reconcile();
            }
        }

        internal void Stop(FakeUiThemeScope scope)
        {
            lock (gate)
            {
                for (var index = scopes.Count - 1; index >= 0; index--)
                {
                    var candidate = scopes[index];
                    if (!IsInScope(candidate, scope)) continue;
                    scopes.RemoveAt(index);
                    candidate.Stopped = true;
                    candidate.Selection = null;
                    candidate.Surfaces.Clear();
                    foreach (var preload in candidate.Preloads.Values)
                    {
                        foreach (var waiter in preload.Waiters) waiter.Complete(Cancelled);
                    }
                    candidate.Preloads.Clear();
                }
                Reconcile();
            }
        }

        private void Cancel(FakeUiThemeScope scope, string id, FakeUiThemeWaiter waiter)
        {
            lock (gate)
            {
                if (!scope.Preloads.TryGetValue(id, out var preload) || !preload.Waiters.Remove(waiter)) return;
                if (!preload.Retained && preload.Waiters.Count == 0) scope.Preloads.Remove(id);
                waiter.Complete(Cancelled);
                Reconcile();
            }
        }

        private void FinishWaiters(string id, OperationResult<bool> result)
        {
            foreach (var scope in scopes)
            {
                if (!scope.Preloads.TryGetValue(id, out var preload)) continue;
                foreach (var waiter in preload.Waiters) waiter.Complete(result);
                preload.Waiters.Clear();
                preload.Retained = result.Succeeded;
                if (!preload.Retained) scope.Preloads.Remove(id);
            }
        }

        private void Reconcile(string? retry = null)
        {
            demanded.Clear();
            foreach (var scope in scopes)
            {
                Demand(scope.Selection?.Id);
                foreach (var preload in scope.Preloads.Keys) Demand(preload);
                foreach (var surface in scope.Surfaces) Demand(EffectiveId(scope, surface.Request.Theme));
            }

            foreach (var registration in registrations.Values)
            {
                if (demanded.Contains(registration))
                {
                    fixturesFrozen = true;
                    if (registration.State == FakeUiThemeLoadState.Idle ||
                        (registration.State == FakeUiThemeLoadState.Failed && registration.Definition.Descriptor.Id == retry))
                        registration.State = FakeUiThemeLoadState.Loading;
                }
                else if (registration.State != FakeUiThemeLoadState.Draining)
                {
                    registration.Loaded = null;
                    registration.State = registration.State == FakeUiThemeLoadState.Loading
                        ? FakeUiThemeLoadState.Draining
                        : FakeUiThemeLoadState.Idle;
                }
            }
        }

        private void Demand(string? id)
        {
            if (id != null && registrations.TryGetValue(id, out var registration)) demanded.Add(registration);
        }

        private string? EffectiveId(FakeUiThemeScope scope, UiThemeRef? surfaceTheme)
        {
            if (scope.Stopped) return null;
            if (surfaceTheme != null) return surfaceTheme.Id;
            for (FakeUiThemeScope? current = scope; current != null; current = current.Parent)
            {
                if (current.Selection != null) return current.Selection.Id;
            }
            return manifestDefault;
        }

        private static bool IsInScope(FakeUiThemeScope candidate, FakeUiThemeScope scope)
        {
            for (FakeUiThemeScope? current = candidate; current != null; current = current.Parent)
            {
                if (ReferenceEquals(current, scope)) return true;
            }
            return false;
        }
    }

    internal sealed class FakeUiThemeDefinition
    {
        internal FakeUiThemeDefinition(byte[] bytes, UiThemeDescriptor descriptor)
        {
            Bytes = bytes;
            Descriptor = descriptor;
        }

        internal byte[] Bytes { get; }
        internal UiThemeDescriptor Descriptor { get; }
    }

    internal sealed class FakeUiThemeRegistration
    {
        internal FakeUiThemeRegistration(FakeUiThemeDefinition definition) { Definition = definition; }

        internal FakeUiThemeDefinition Definition { get; }
        internal FakeUiThemeLoadState State { get; set; }
        internal UiThemeDescriptor? Loaded { get; set; }
    }

    internal enum FakeUiThemeLoadState { Idle, Loading, Ready, Failed, Draining }

    internal readonly struct FakeUiThemeResolution
    {
        internal FakeUiThemeResolution(UiThemeDescriptor descriptor, string? requestedId, bool usedFallback)
        {
            Descriptor = descriptor;
            RequestedId = requestedId;
            UsedFallback = usedFallback;
        }

        internal UiThemeDescriptor Descriptor { get; }
        internal string? RequestedId { get; }
        internal bool UsedFallback { get; }
    }
}
