﻿using System;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.Linq;
using System.Reflection;
using ComputeSharp.Shaders.Mappings;
using ComputeSharp.Shaders.Translation.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ComputeSharp.Shaders.Extensions
{
    /// <summary>
    /// A <see langword="class"/> with some extension methods for C# syntax nodes
    /// </summary>
    internal static class SyntaxNodeExtensions
    {
        /// <summary>
        /// Checks a <see cref="SyntaxNode"/> value and replaces the value type to be HLSL compatible, if needed
        /// </summary>
        /// <typeparam name="TRoot">The type of the input <see cref="SyntaxNode"/> instance</typeparam>
        /// <param name="node">The input <see cref="SyntaxNode"/> to check and modify if needed</param>
        /// <param name="type">The <see cref="TypeSyntax"/> to use for the input node</param>
        /// <returns>A <see cref="SyntaxNode"/> instance that represents a type compatible with HLSL</returns>
        [Pure]
        public static TRoot ReplaceType<TRoot>(this TRoot node, TypeSyntax type) where TRoot : SyntaxNode
        {
            string value = HlslKnownTypes.GetMappedName(type.ToString());

            // If the HLSL mapped full type name equals the original type, just return the input node
            if (value == type.ToString()) return node;

            // Process and return the type name
            TypeSyntax newType = SyntaxFactory.ParseTypeName(value).WithLeadingTrivia(type.GetLeadingTrivia()).WithTrailingTrivia(type.GetTrailingTrivia());
            return node.ReplaceNode(type, newType);
        }

        /// <summary>
        /// Checks a <see cref="MemberAccessExpressionSyntax"/> instance and replaces it to be HLSL compatible, if needed
        /// </summary>
        /// <param name="node">The input <see cref="MemberAccessExpressionSyntax"/> to check and modify if needed</param>
        /// <param name="semanticModel">The <see cref="SemanticModel"/> to use to load symbols for the input node</param>
        /// <param name="variable">The info on parsed static members, if any</param>
        /// <returns>A <see cref="SyntaxNode"/> instance that is compatible with HLSL</returns>
        [Pure]
        public static SyntaxNode ReplaceMember(this MemberAccessExpressionSyntax node, SemanticModel semanticModel, out (string Name, ReadableMember MemberInfo)? variable)
        {
            // Set the variable to null, replace it later on if needed
            variable = null;

            SymbolInfo containingMemberSymbolInfo;
            ISymbol? memberSymbol;
            try
            {
                containingMemberSymbolInfo = semanticModel.GetSymbolInfo(node.Expression);
                SymbolInfo memberSymbolInfo = semanticModel.GetSymbolInfo(node.Name);
                memberSymbol = memberSymbolInfo.Symbol ?? memberSymbolInfo.CandidateSymbols.FirstOrDefault();
            }
            catch (ArgumentException)
            {
                // Member access on a captured HLSL-compatible field or property
                string name = node.Name.ToFullString();
                if (name == "X" || name == "Y" || name == "Z" || name == "W") return node.WithName(SyntaxFactory.IdentifierName(name.ToLowerInvariant()));
                return node;
            }

            // If the input member has no symbol, try to load it manually
            if (memberSymbol is null)
            {
                string expression = node.WithoutTrivia().ToFullString();
                int index = expression.LastIndexOf(Type.Delimiter);
                string fullname = expression.Substring(0, index);
                if (!(AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullname)).FirstOrDefault(t => t != null) is Type type))
                {
                    // The current node can't possibly represent a field or a property, so just return it
                    return node;
                }

                // Try to get the target static field or property, if present
                string name = expression.Substring(index + 1, expression.Length - fullname.Length - 1);
                MemberInfo[] memberInfos = type.GetMember(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (memberInfos.Length == 0) return node;
                bool isReadonly;
                ReadableMember memberInfo;
                switch (memberInfos.First())
                {
                    case FieldInfo fieldInfo:
                        isReadonly = fieldInfo.IsInitOnly;
                        memberInfo = fieldInfo;
                        break;
                    case PropertyInfo propertyInfo:
                        isReadonly = !propertyInfo.CanWrite;
                        memberInfo = propertyInfo;
                        break;
                    default: throw new InvalidOperationException($"Invalid symbol kind: {memberInfos.First().GetType()}");
                }

                // Handle the loaded info
                return ProcessStaticMember(node, memberInfo, isReadonly, ref variable);
            }

            // If the input member is not a field, property or method, just return it
            if (memberSymbol.Kind != SymbolKind.Field &&
                memberSymbol.Kind != SymbolKind.Property &&
                memberSymbol.Kind != SymbolKind.Method)
            {
                return node;
            }

            // Process the input node if it's a known method invocation
            if (HlslKnownMethods.TryGetMappedName(containingMemberSymbolInfo.Symbol, memberSymbol, out string? mappedName))
            {
                string expression = memberSymbol.IsStatic ? mappedName : $"{node.Expression}{mappedName}";
                return SyntaxFactory.IdentifierName(expression).WithLeadingTrivia(node.GetLeadingTrivia()).WithTrailingTrivia(node.GetTrailingTrivia());
            }

            // Handle static fields as a special case
            if (memberSymbol.IsStatic && (
                memberSymbol.Kind == SymbolKind.Field ||
                memberSymbol.Kind == SymbolKind.Property))
            {
                // Get the containing type
                string
                    typeFullname = memberSymbol.ContainingType.ToString(),
                    assemblyFullname = memberSymbol.ContainingAssembly.ToString();
                Type fieldDeclaringType = Type.GetType($"{typeFullname}, {assemblyFullname}");

                // Retrieve the field or property info
                bool isReadonly;
                ReadableMember memberInfo;
                switch (memberSymbol.Kind)
                {
                    case SymbolKind.Field:
                        FieldInfo fieldInfo = fieldDeclaringType.GetField(memberSymbol.Name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        isReadonly = fieldInfo.IsInitOnly;
                        memberInfo = fieldInfo;
                        break;
                    case SymbolKind.Property:
                        PropertyInfo propertyInfo = fieldDeclaringType.GetProperty(memberSymbol.Name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        isReadonly = !propertyInfo.CanWrite;
                        memberInfo = propertyInfo;
                        break;
                    default: throw new InvalidOperationException($"Invalid symbol kind: {memberSymbol.Kind}");
                }

                // Handle the loaded info
                return ProcessStaticMember(node, memberInfo, isReadonly, ref variable);
            }

            return node;
        }

        /// <summary>
        /// Processes a loaded static member, either a field or a property
        /// </summary>
        /// <param name="node">The input <see cref="MemberAccessExpressionSyntax"/> to check and modify if needed</param>
        /// <param name="memberInfo">The wrapped member that needs to be processed</param>
        /// <param name="isReadonly">Indicates whether or not the target member is readonly</param>
        /// <param name="variable">The info on parsed static members, if any</param>
        /// <returns>A <see cref="SyntaxNode"/> instance that is compatible with HLSL</returns>
        private static SyntaxNode ProcessStaticMember(SyntaxNode node, ReadableMember memberInfo, bool isReadonly, ref (string Name, ReadableMember MemberInfo)? variable)
        {
            // Constant replacement if the value is a readonly scalar value
            if (isReadonly && HlslKnownTypes.IsKnownScalarType(memberInfo.MemberType))
            {
                LiteralExpressionSyntax expression = memberInfo.GetValue(null) switch
                {
                    true => SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression, SyntaxFactory.Token(SyntaxKind.TrueKeyword)),
                    false => SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression, SyntaxFactory.Token(SyntaxKind.TrueKeyword)),
                    IFormattable scalar => SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.ParseToken(scalar.ToString(null, CultureInfo.InvariantCulture))),
                    _ => throw new InvalidOperationException($"Invalid field of type {memberInfo.MemberType}")
                };
                return expression.WithLeadingTrivia(node.GetLeadingTrivia()).WithTrailingTrivia(node.GetTrailingTrivia());
            }

            // Captured member, treat it like any other captured variable in the closure
            string name = $"{memberInfo.DeclaringType.Name}_{memberInfo.Name}";
            variable = (name, memberInfo);
            return SyntaxFactory.IdentifierName(name).WithLeadingTrivia(node.GetLeadingTrivia()).WithTrailingTrivia(node.GetTrailingTrivia());
        }
    }
}
