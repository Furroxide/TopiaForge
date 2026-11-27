using System;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class SdkPublicApiBaselineTests
    {
        private static void SignatureIdentityBaselineTests()
        {
            const string header = "type public class Example.Service : System.Object\n";
            const string old = header + "  method System.Void Run(System.String name)\n";
            ExpectRejected(old, header + "  method System.Void Run(System.Int32 count)\n", MustAdd,
                "a new signature replacing a removed overload");
            ExpectRejected(old, header + "  method System.Void Run(System.String label) [requires-topiaforge=\"0.2.0\"]\n"
                + "  method System.Void Run(System.Int32 count)\n", MustNotGain,
                "a marker on the old overload hiding an unmarked new overload");
            const string indexers = header + "  property System.Int32 Item[System.Int32 index] { get; set; }\n"
                + "  property System.Int32 Item[System.String key] { get; }\n";
            ExpectRejected(indexers, indexers.Replace("Item[System.String key] { get; }", "Item[System.String key] { get; set; }",
                StringComparison.Ordinal), "adds the set accessor", "a setter borrowed from a different indexer");
            ExpectRejected(header + "  constructor .ctor(System.String name)\n", header + "  constructor .ctor(System.Int32 count)\n",
                MustAdd, "a constructor with a new parameter type");
            ExpectRejected(header + "  method [static] System.Int32 op_Implicit(Example.Service value)\n",
                header + "  method [static] System.String op_Implicit(Example.Service value)\n", MustAdd,
                "a conversion with a new return type");
            const string generic = "type public class Example.Service<T> : System.Object\n"
                + "  method U Map<U>(T item, System.Func<T, U> map)\n";
            RequireVersionedAdditions(generic, generic.Replace("<T>", "<TItem>", StringComparison.Ordinal)
                .Replace("U Map<U>(T item, System.Func<T, U> map)", "TResult Map<TResult>(TItem value, System.Func<TItem, TResult> convert)", StringComparison.Ordinal));
            ExpectRejected("type public class Example.Service<T>\n  method System.Void Run(T.Component value)\n",
                "type public class Example.Service<U>\n  method System.Void Run(U.Component value)\n", MustAdd,
                "a changed namespace mistaken for a renamed generic parameter");
            RequireVersionedAdditions(old, old.Replace("class Example", "sealed class Example", StringComparison.Ordinal));
            ExpectRejected(old, old.Replace("class Example", "interface Example", StringComparison.Ordinal), MustAdd,
                "a new type kind with an existing name");
            const string complex = header + "  method System.Void Run(System.Collections.Generic.Dictionary<System.String, System.Int32[]> values, System.String text = \"a, b (c)\")\n";
            RequireVersionedAdditions(complex, complex.Replace(" values,", " entries,", StringComparison.Ordinal)
                .Replace(" text = \"a, b (c)\"", " label = \"changed\"", StringComparison.Ordinal));
            ExpectRejected(complex, complex.Replace("System.Int32[]", "System.Int64[]", StringComparison.Ordinal), MustAdd,
                "a parameter type inside a generic signature");
            RequireVersionedAdditions(header + "  method System.Void Run(System.Int32[,] values)\n",
                header + "  method System.Void Run(System.Int32[,] entries)\n");
            ExpectRejected(header + "  method System.Void Run(System.String text = \"[static]\")\n",
                header + "  method [static] System.Void Run(System.String text = \"\")\n", MustAdd,
                "a static replacement hidden by an instance method's default string");
            ExpectRejected(header + "  field const System.Int32 Value = 1\n",
                header + "  field System.Int32 Value\n", MustAdd,
                "an instance field replacing a static constant");
            const string explicitMember = header + "  method System.Void Example.IHandler<System.Int32>.Run(System.String text)\n";
            ExpectRejected(explicitMember, explicitMember.Replace("System.String text", "System.Int32 number", StringComparison.Ordinal), MustAdd,
                "an explicit member with a new signature");
            const string byValue = header + "  method System.Void Run(System.Int32 count)\n";
            ExpectRejected(byValue, byValue + "  method System.Void Run(ref System.Int32 count)\n", MustAdd,
                "a new by-reference overload beside a by-value one");
            RequireVersionedAdditions(header + "  method System.Void Run(System.String[] values)\n",
                header + "  method System.Void Run(params System.String[] values)\n");
            const string eventLine = "  event System.Action Changed { add; }\n";
            ExpectRejected(header + eventLine, header + eventLine.Replace("add;", "add; remove;", StringComparison.Ordinal),
                "adds the remove accessor", "an unmarked event remover");
            RequireVersionedAdditions(header + eventLine, header + eventLine.Replace("add;", "add; remove [requires-topiaforge=\"0.2.0\"];", StringComparison.Ordinal));
            var outerGeneric = FormatType(typeof(IdentityContainer<>.Nested), null);
            var innerGeneric = FormatType(typeof(IdentityContainer.Nested<>), null);
            if (outerGeneric == innerGeneric) throw new InvalidOperationException("Nested type identities must retain the declaring type's generic arity.");
            ExpectRejected("type public class " + outerGeneric + "\n", "type public class " + innerGeneric + "\n", MustAdd,
                "a new nested type with generic parameters on a different declaring type");
            var twoLevels = FormatType(typeof(IdentityContainer<int>.Inner<string>), null);
            if (!twoLevels.EndsWith("IdentityContainer<System.Int32>.Inner<System.String>", StringComparison.Ordinal))
                throw new InvalidOperationException("Each declaring type must keep its own generic arguments: " + twoLevels);
            Console.WriteLine("SDK signature identity checks passed (22 cases).");
        }

        private sealed class IdentityContainer<T> { public sealed class Nested { } public sealed class Inner<U> { } }
        private sealed class IdentityContainer { public sealed class Nested<T> { } }
    }
}
