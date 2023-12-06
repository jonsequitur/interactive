﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#nullable enable

using Microsoft.CodeAnalysis.Text;
using Microsoft.DotNet.Interactive.Parsing;

namespace Microsoft.DotNet.Interactive.Http.Parsing;

internal class HttpSyntaxTree : SyntaxTree
{
    public HttpSyntaxTree(SourceText sourceText)
    {
        RootNode = new HttpRootSyntaxNode(sourceText, this);
    }

    public HttpRootSyntaxNode RootNode { get; }
}