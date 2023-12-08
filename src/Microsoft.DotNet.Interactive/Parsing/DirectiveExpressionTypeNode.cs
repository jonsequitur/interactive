// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#nullable enable
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.DotNet.Interactive.Parsing;

[DebuggerStepThrough]
internal class KernelNameDirectiveNode : DirectiveNode
{
    internal KernelNameDirectiveNode(
        SourceText sourceText,
        PolyglotSyntaxTree? syntaxTree) : base(sourceText, syntaxTree)
    {
    }

    public string Type => _type ??= Text.TrimStart('@').TrimEnd(':');
}