﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.DotNet.Interactive.Commands;
using Microsoft.SemanticKernel;

namespace Microsoft.DotNet.Interactive.OpenAI;

public class OpenAIKernel : 
    Kernel,
    IKernelCommandHandler<SubmitCode>
{
    private readonly IKernel _semanticKernel;

    public OpenAIKernel(string name = "openai") : base(name)
    {
        KernelInfo.LanguageName = "text";
        _semanticKernel = SemanticKernel.Kernel.Builder.Build();
    }

    public void Configure(OpenAIKernelSettings settings)
    {
        throw new NotImplementedException();
    }



    public async Task HandleAsync(SubmitCode command, KernelInvocationContext context)
    {
        var result = await _semanticKernel.RunAsync(command.Code, context.CancellationToken);
        // we need probably more info at this point, if dall-e is used we need to display image
        throw new NotImplementedException();
    }
}

public record OpenAIKernelSettings(string model, string endpoint, string apiKey, bool useAzureOpenAI = false, string? orgId = null);