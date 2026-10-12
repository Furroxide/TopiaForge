using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    internal sealed partial class SessionEngine
    {
        private readonly Dictionary<WireKey, StateRecord> states = new Dictionary<WireKey, StateRecord>();
        private readonly List<StateRecord> stateList = new List<StateRecord>();
        private readonly Dictionary<WireKey, CommandBinding> commands = new Dictionary<WireKey, CommandBinding>();
        private readonly Dictionary<WireKey, ObjectTypeBinding> objectTypes = new Dictionary<WireKey, ObjectTypeBinding>();
        private readonly Dictionary<WireKey, PresentationBinding> presentations = new Dictionary<WireKey, PresentationBinding>();
        private readonly Dictionary<WireKey, ContractRegistration> contracts = new Dictionary<WireKey, ContractRegistration>();
        private readonly Dictionary<WireKey, List<Delegate>> objectSubscribers = new Dictionary<WireKey, List<Delegate>>();

        internal OperationResult<IReplicatedState<T>> RegisterState<T>(string owner, ReplicatedStateDefinition<T> definition)
            where T : class
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            RequireOwnerThread();
            var refusal = RegistrationRefusal<IReplicatedState<T>>("Replicated state");
            if (refusal != null) return refusal;
            var key = new WireKey(owner, definition.Id);
            if (states.ContainsKey(key))
            {
                return OperationResult<IReplicatedState<T>>.Failure(
                    ModErrorCode.Conflict,
                    "Replicated state '" + definition.Id + "' is already registered.");
            }

            var encoded = EngineCodec.Encode(definition.Codec, definition.InitialValue);
            if (!encoded.TryGetValue(out var initial))
            {
                return OperationResult<IReplicatedState<T>>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
            }

            var check = EngineCodec.Decode(definition.Codec, initial);
            if (!check.Succeeded) return OperationResult<IReplicatedState<T>>.Failure(check.ErrorCode, check.ErrorMessage);
            var record = new StateRecord<T>(this, key, definition, initial);
            states.Add(key, record);
            stateList.Add(record);
            if (Role == EngineRole.Host) OnHostStateRegistered(record);
            else AdoptUnboundState(record);
            record.NotifiedBytes = record.CurrentBytes;
            record.NotifiedVersion = record.CurrentVersion;
            EndOperation();
            return OperationResult<IReplicatedState<T>>.Success(record);
        }

        internal OperationResult<IMultiplayerCommandRegistration> RegisterCommand<TRequest, TResponse>(
            string owner,
            MultiplayerCommandDefinition<TRequest, TResponse> definition)
            where TRequest : class
            where TResponse : class
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            RequireOwnerThread();
            var refusal = RegistrationRefusal<IMultiplayerCommandRegistration>("A command");
            if (refusal != null) return refusal;
            var key = new WireKey(owner, definition.Id);
            if (commands.ContainsKey(key))
            {
                return OperationResult<IMultiplayerCommandRegistration>.Failure(
                    ModErrorCode.Conflict,
                    "Command '" + definition.Id + "' is already registered.");
            }

            var binding = new CommandBinding<TRequest, TResponse>(this, key, definition);
            commands.Add(key, binding);
            return OperationResult<IMultiplayerCommandRegistration>.Success(binding);
        }

        internal OperationResult<IReplicatedObjectTypeRegistration> RegisterObjectType<TState, TInput>(
            string owner,
            ReplicatedObjectTypeDefinition<TState, TInput> definition)
            where TState : class
            where TInput : class
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            RequireOwnerThread();
            var refusal = RegistrationRefusal<IReplicatedObjectTypeRegistration>("A replicated-object type");
            if (refusal != null) return refusal;
            var key = new WireKey(owner, definition.TypeId);
            if (objectTypes.ContainsKey(key))
            {
                return OperationResult<IReplicatedObjectTypeRegistration>.Failure(
                    ModErrorCode.Conflict,
                    "Replicated-object type '" + definition.TypeId + "' is already registered.");
            }

            var binding = new ObjectTypeBinding<TState, TInput>(this, key, definition);
            objectTypes.Add(key, binding);
            if (Role == EngineRole.Client) AdoptUnboundObjects(binding);
            EndOperation();
            return OperationResult<IReplicatedObjectTypeRegistration>.Success(binding);
        }

        internal OperationResult<IPresentationEventRegistration> RegisterPresentation<TEvent>(
            string owner,
            PresentationEventDefinition<TEvent> definition)
            where TEvent : class
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            RequireOwnerThread();
            var refusal = RegistrationRefusal<IPresentationEventRegistration>("A presentation event");
            if (refusal != null) return refusal;
            if (!HasPresentation && definition.Handler != null)
            {
                return OperationResult<IPresentationEventRegistration>.Failure(
                    ModErrorCode.Unavailable,
                    "Presentation handlers are unavailable on a headless multiplayer process.");
            }

            var key = new WireKey(owner, definition.Type.Id);
            if (presentations.ContainsKey(key))
            {
                return OperationResult<IPresentationEventRegistration>.Failure(
                    ModErrorCode.Conflict,
                    "Presentation event '" + definition.Type.Id + "' is already registered.");
            }

            var binding = new PresentationBinding<TEvent>(this, key, definition);
            presentations.Add(key, binding);
            return OperationResult<IPresentationEventRegistration>.Success(binding);
        }

        internal OperationResult<IDisposable> RegisterContract(string owner, MultiplayerContractDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            RequireOwnerThread();
            var refusal = RegistrationRefusal<IDisposable>("A multiplayer contract");
            if (refusal != null) return refusal;
            var key = new WireKey(owner, descriptor.Id);
            if (contracts.ContainsKey(key))
            {
                return OperationResult<IDisposable>.Failure(
                    ModErrorCode.Conflict,
                    "Multiplayer contract '" + descriptor.Id + "' is already registered.");
            }

            var registration = new ContractRegistration(key, descriptor);
            contracts.Add(key, registration);
            return OperationResult<IDisposable>.Success(new EngineLease(() => Post(() =>
            {
                if (contracts.TryGetValue(key, out var current) && ReferenceEquals(current, registration)) contracts.Remove(key);
            })));
        }

        internal IDisposable SubscribeObjects<TState, TInput>(
            string owner,
            ReplicatedObjectType<TState, TInput> type,
            Action<ReplicatedObjectChange<TState, TInput>> handler)
            where TState : class
            where TInput : class
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            RequireOwnerThread();
            if (ended) throw new ObjectDisposedException(nameof(IMultiplayerSession));
            var key = new WireKey(owner, type.Id);
            if (!objectSubscribers.TryGetValue(key, out var subscribers))
            {
                subscribers = new List<Delegate>();
                objectSubscribers.Add(key, subscribers);
            }

            subscribers.Add(handler);
            return new EngineLease(() => Post(() => subscribers.Remove(handler)));
        }

        internal void UnregisterState(StateRecord record) => Post(() =>
        {
            if (!record.IsActive) return;
            record.IsActive = false;
            if (states.TryGetValue(record.Key, out var current) && ReferenceEquals(current, record)) states.Remove(record.Key);
            stateList.Remove(record);
        });

        internal void UnregisterCommand(CommandBinding binding) => Post(() =>
        {
            if (!binding.IsActive) return;
            binding.IsActive = false;
            if (commands.TryGetValue(binding.Key, out var current) && ReferenceEquals(current, binding)) commands.Remove(binding.Key);
        });

        internal void UnregisterObjectType(ObjectTypeBinding binding) => Post(() =>
        {
            if (!binding.IsActive) return;
            binding.IsActive = false;
            if (objectTypes.TryGetValue(binding.Key, out var current) && ReferenceEquals(current, binding)) objectTypes.Remove(binding.Key);
        });

        internal void UnregisterPresentation(PresentationBinding binding) => Post(() =>
        {
            if (!binding.IsActive) return;
            binding.IsActive = false;
            if (presentations.TryGetValue(binding.Key, out var current) && ReferenceEquals(current, binding)) presentations.Remove(binding.Key);
        });

        internal List<ContractEntry> RegisteredContracts()
        {
            var entries = new List<ContractEntry>(contracts.Count);
            foreach (var registration in contracts.Values) entries.Add(registration.ToEntry());
            return entries;
        }

        private OperationResult<T>? RegistrationRefusal<T>(string what) where T : notnull
        {
            if (ended) return OperationResult<T>.Failure(ModErrorCode.InvalidState, "The multiplayer session has ended.");
            if (scope.IsOpen)
            {
                return OperationResult<T>.Failure(
                    ModErrorCode.InvalidState,
                    what + " cannot be registered inside a multiplayer transaction.");
            }

            return null;
        }
    }
}
