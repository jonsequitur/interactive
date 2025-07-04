// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.DotNet.Interactive.CSharpProject.Models;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.Formatting;
using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;

namespace Microsoft.DotNet.Interactive.CSharpProject.LanguageServices;

internal static class CompletionExtensions
{
    private static readonly string SymbolCompletionProvider = "Microsoft.CodeAnalysis.CSharp.Completion.Providers.SymbolCompletionProvider";
    private static readonly string Provider = nameof(Provider);
    private static readonly string SymbolName = nameof(SymbolName);
    private static readonly string Symbols = nameof(Symbols);
    private static readonly string GetSymbolsAsync = nameof(GetSymbolsAsync);
    private static readonly PropertyInfo providerNameAccessor = typeof(RoslynCompletionItem).GetProperty("ProviderName", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly ImmutableArray<string> KindTags = ImmutableArray.Create(
        WellKnownTags.Class,
        WellKnownTags.Constant,
        WellKnownTags.Delegate,
        WellKnownTags.Enum,
        WellKnownTags.EnumMember,
        WellKnownTags.Event,
        WellKnownTags.ExtensionMethod,
        WellKnownTags.Field,
        WellKnownTags.Interface,
        WellKnownTags.Intrinsic,
        WellKnownTags.Keyword,
        WellKnownTags.Label,
        WellKnownTags.Local,
        WellKnownTags.Method,
        WellKnownTags.Module,
        WellKnownTags.Namespace,
        WellKnownTags.Operator,
        WellKnownTags.Parameter,
        WellKnownTags.Property,
        WellKnownTags.RangeVariable,
        WellKnownTags.Reference,
        WellKnownTags.Structure,
        WellKnownTags.TypeParameter,
        WellKnownTags.Snippet);

    public static string GetKind(this RoslynCompletionItem completionItem)
    {
        for (var i = 0; i < KindTags.Length; i++)
        {
            var tag = KindTags[i];

            if (completionItem.Tags.Contains(tag))
            {
                return tag;
            }
        }

        return null;
    }

    public static CompletionItem ToModel(
        this RoslynCompletionItem item, 
        Dictionary<(string, int), ISymbol> recommendedSymbols,
        Document document)
    {
        var documentation =  GetDocumentation(item, recommendedSymbols, document);

        return new CompletionItem(
            displayText: item.DisplayText,
            kind: item.GetKind(),
            filterText: item.FilterText,
            sortText: item.SortText,
            insertText: item.FilterText,
            documentation: documentation?.Value);
    }

    public static FormattedValue GetDocumentation(
        this RoslynCompletionItem item,
        Dictionary<(string, int), ISymbol> recommendedSymbols,
        Document document)
    {
        var symbol = GetCompletionSymbolAsync(item, recommendedSymbols, document);
            
        if (symbol is not null)
        {
            return DocumentationConverter.GetDocumentation(symbol, "\n");
        }

        return null;
    }

    public static  ISymbol GetCompletionSymbolAsync(
        RoslynCompletionItem completionItem, 
        Dictionary<(string, int), ISymbol> recommendedSymbols,
        Document document)
    {
        var provider = GetProviderName(completionItem);
        if (provider == SymbolCompletionProvider)
        {
            var properties = completionItem.Properties;
            if (recommendedSymbols.TryGetValue((properties[SymbolName], int.Parse(properties[nameof(SymbolKind)])), out var symbol))
            {
                // We were able to match this SymbolCompletionProvider item with a recommended symbol
                return symbol;
            }
        }

        return null;
    }

    private static string GetProviderName(RoslynCompletionItem item)
    {
        return (string)providerNameAccessor.GetValue(item);
    }
}