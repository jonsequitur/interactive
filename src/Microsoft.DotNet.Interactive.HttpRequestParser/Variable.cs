﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DotNet.Interactive.HttpRequest;

internal record Variable(ParseItem Name, ParseItem Value, string ExpandedValue)
{
    public string VariableName => Name.Text.Substring(1);
}
