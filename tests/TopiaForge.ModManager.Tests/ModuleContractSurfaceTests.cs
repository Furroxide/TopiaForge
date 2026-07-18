using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static class ModuleContractSurfaceTests
    {
        private static readonly string[] ForbiddenAssemblyPrefixes =
        {
            "UnityEngine",
            "GameCode",
            "0Harmony",
            "Harmony"
        };

        private static readonly HashSet<string> ForbiddenMemberNames = new HashSet<string>(
            new[]
            {
                "UnloadOwner",
                "UnregisterOwner",
                "ReleaseOwner",
                "ForceReset",
                "ClearAssetOverrides"
            },
            StringComparer.Ordinal);

        public static void Run()
        {
            var modules = new[]
            {
                typeof(IRobotAgentService).Assembly,
                typeof(IWorldGamemodeService).Assembly,
                typeof(ITimeControlService).Assembly,
                typeof(IPromptOverrideRegistry).Assembly,
                typeof(IUgcLiveSyncService).Assembly
            };

            Assert(modules.Select(assembly => assembly.GetName().Name).Distinct(StringComparer.Ordinal).Count() == 5,
                "each module contract must live in its own assembly");

            foreach (var module in modules)
            {
                AssertContractAssembly(module);
            }

            var core = typeof(IModContext).Assembly;
            AssertContractAssembly(core);
            AssertContractAssembly(typeof(FakeModContext).Assembly);
            AssertExpectedFailureContracts(core);
            foreach (var specialized in new[]
                     {
                         typeof(IRobotAgentService),
                         typeof(IWorldGamemodeService),
                         typeof(ITimeControlService),
                         typeof(IPromptOverrideRegistry),
                         typeof(IUgcLiveSyncService)
                     })
            {
                Assert(specialized.Assembly != core,
                    specialized.FullName + " must not leak back into TopiaForge.Mods.Abstractions");
            }

            Console.WriteLine("All module contract surface tests passed.");
        }

        private static void AssertExpectedFailureContracts(Assembly core)
        {
            Assert(typeof(IInputService).GetMethod(nameof(IInputService.RegisterAction))?.ReturnType ==
                   typeof(OperationResult<IInputAction>),
                "input registration must report expected conflicts and unavailable bindings through OperationResult");
            Assert(typeof(ILocalizationService).GetMethod(nameof(ILocalizationService.Register))?.ReturnType ==
                   typeof(OperationResult<ILocalizationRegistration>),
                "localization registration must report lifetime cancellation through OperationResult");

            foreach (var methodName in new[]
                     {
                         nameof(IModScheduler.NextFrame),
                         nameof(IModScheduler.After),
                         nameof(IModScheduler.Every)
                     })
            {
                var method = typeof(IModScheduler).GetMethod(methodName);
                Assert(method?.ReturnType == typeof(OperationResult<IDisposable>),
                    "scheduler operation " + methodName + " must report unavailable and cancelled states through OperationResult");
            }

            Assert(typeof(IInputService).Assembly == core && typeof(IModScheduler).Assembly == core,
                "expected-failure contract checks must target the core safe SDK assembly");
        }

        private static void AssertContractAssembly(Assembly assembly)
        {
            Assert(assembly.GetName().Version == new Version(1, 0, 0, 0),
                assembly.GetName().Name + " must keep the V1 assembly version stable");
            Assert(File.Exists(Path.ChangeExtension(assembly.Location, ".xml")),
                assembly.GetName().Name + " must emit IntelliSense XML documentation");

            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                Assert(!ForbiddenAssemblyPrefixes.Any(prefix =>
                        (reference.Name ?? string.Empty).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)),
                    assembly.GetName().Name + " must not reference " + reference.Name);
            }

            foreach (var type in assembly.GetExportedTypes())
            {
                if (IsImmutableDataContract(type))
                {
                    foreach (var property in type.GetProperties(
                                 BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        Assert(property.SetMethod?.IsPublic != true,
                            type.FullName + "." + property.Name +
                            " must be immutable; supply values through its constructor or factory");
                    }
                }

                if (type.BaseType != typeof(object))
                {
                    AssertSafeType(type.BaseType, type.FullName + " base type");
                }
                foreach (var contract in type.GetInterfaces())
                {
                    AssertSafeType(contract, type.FullName + " interface");
                }

                foreach (var member in type.GetMembers(
                             BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    Assert(!ForbiddenMemberNames.Contains(member.Name) || IsExplicitTestingFaultControl(type, member),
                        type.FullName + "." + member.Name + " exposes a global destructive operation");

                    switch (member)
                    {
                        case FieldInfo field:
                            AssertSafeType(field.FieldType, type.FullName + "." + field.Name);
                            break;
                        case PropertyInfo property:
                            AssertSafeType(property.PropertyType, type.FullName + "." + property.Name);
                            foreach (var parameter in property.GetIndexParameters())
                            {
                                AssertSafeType(parameter.ParameterType, type.FullName + "." + property.Name);
                            }

                            break;
                        case EventInfo eventInfo:
                            AssertSafeType(eventInfo.EventHandlerType, type.FullName + "." + eventInfo.Name);
                            break;
                        case MethodBase method:
                            if (IsOrdinaryObjectEquals(method))
                            {
                                break;
                            }

                            if (method is MethodInfo methodInfo)
                            {
                                AssertSafeType(methodInfo.ReturnType, type.FullName + "." + method.Name + " return");
                            }

                            foreach (var parameter in method.GetParameters())
                            {
                                Assert(!string.Equals(parameter.Name, "ownerId", StringComparison.OrdinalIgnoreCase) &&
                                       !string.Equals(parameter.Name, "ownerModId", StringComparison.OrdinalIgnoreCase) &&
                                       !string.Equals(parameter.Name, "packageRoot", StringComparison.OrdinalIgnoreCase),
                                    type.FullName + "." + method.Name +
                                    " accepts caller-supplied ownership or package-root state");
                                AssertSafeType(parameter.ParameterType, type.FullName + "." + method.Name);
                            }

                            break;
                    }
                }
            }
        }

        private static bool IsOrdinaryObjectEquals(MethodBase method)
        {
            var parameters = method.GetParameters();
            return method.Name == nameof(object.Equals)
                && parameters.Length == 1
                && parameters[0].ParameterType == typeof(object);
        }

        private static bool IsExplicitTestingFaultControl(Type type, MemberInfo member)
        {
            // Testing fakes intentionally expose narrowly scoped fault/reset seams so mods can prove stale-handle
            // recovery. Keep the production contract ban intact and allow only this named fake operation.
            return type == typeof(FakeTimeControlService)
                && string.Equals(member.Name, nameof(FakeTimeControlService.ForceReset), StringComparison.Ordinal);
        }

        private static bool IsImmutableDataContract(Type type)
        {
            return type.Name.EndsWith("Request", StringComparison.Ordinal)
                || type.Name.EndsWith("Result", StringComparison.Ordinal)
                || type.Name.EndsWith("Options", StringComparison.Ordinal)
                || type.Name.EndsWith("Definition", StringComparison.Ordinal)
                || type.Name.EndsWith("Snapshot", StringComparison.Ordinal)
                || type == typeof(RobotObjective);
        }

        private static void AssertSafeType(Type? type, string location)
        {
            if (type == null)
            {
                return;
            }

            if (type.IsByRef || type.IsPointer || type.IsArray)
            {
                AssertSafeType(type.GetElementType(), location);
                return;
            }

            Assert(type != typeof(object), location + " exposes raw System.Object");
            Assert(type != typeof(Type), location + " exposes System.Type");
            Assert(!ForbiddenAssemblyPrefixes.Any(prefix =>
                    (type.Assembly.GetName().Name ?? string.Empty).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)),
                location + " exposes " + type.FullName);

            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    AssertSafeType(argument, location);
                }
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
