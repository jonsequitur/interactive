﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;

namespace Microsoft.DotNet.Interactive.Documents;

public class ReturnValueElement : InteractiveDocumentOutputElement
{
    public ReturnValueElement(IDictionary<string, object>? data)
    {
        Data = data ?? throw new ArgumentNullException(nameof(data));
    }

    public IDictionary<string, object> Data { get; }
}