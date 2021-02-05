﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.DotNet.Interactive.Connection;

namespace Microsoft.DotNet.Interactive.ExtensionLab
{
    public class SQLiteConnectionOptions : KernelConnectionOptions
    {
        public string ConnectionString { get; set; }
    }
}