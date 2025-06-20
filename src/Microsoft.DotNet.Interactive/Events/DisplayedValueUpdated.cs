﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Formatting;

namespace Microsoft.DotNet.Interactive.Events;

public class DisplayedValueUpdated : DisplayEvent
{
    public DisplayedValueUpdated(
        object value,
        string valueId,
        KernelCommand command,
        IReadOnlyCollection<FormattedValue> formattedValues = null) : base(value, command, formattedValues, valueId)
    {
        if (string.IsNullOrWhiteSpace(valueId))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(valueId));
        }
    }
}