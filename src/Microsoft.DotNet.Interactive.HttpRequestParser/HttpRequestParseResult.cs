﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Net.Http;

namespace Microsoft.DotNet.Interactive.HttpRequest;

internal class HttpRequestParseResult
{
    public HttpRequestParseResult(HttpSyntaxTree syntaxTree)
    {
        SyntaxTree = syntaxTree;
    }

    public HttpSyntaxTree SyntaxTree { get; }

    public IEnumerable<HttpRequestMessage> GetHttpRequestMessages()
    {
        foreach (var node in SyntaxTree.RootNode.ChildNodes)
        {
            if (node is HttpRequestNode requestNode)
            {
                yield return new HttpRequestMessage(
                    new HttpMethod(requestNode.MethodNode.Text), 
                    new Uri(@"https://something.we.wont.use.in.our.tests.com"));
            }
        }
    }
}