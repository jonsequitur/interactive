﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.Tests.Utility;
using Microsoft.FSharp.Collections;
using Xunit;

namespace Microsoft.DotNet.Interactive.Tests;

public partial class KernelCommandNestingTests
{
    public class Kernel_KernelEvents
    {
        [Fact]
        public async Task Commands_sent_within_the_code_of_another_command_publish_error_events_on_CompositeKernel_for_failures()
        {
            using var kernel = new CompositeKernel
            {
                new CSharpKernel("cs1"),
                new CSharpKernel("cs2")
            };
            var kernelEvents = kernel.KernelEvents.ToSubscribedList();
            var command = new SubmitCode(
                $"""
                 #!cs1
                 using {typeof(Kernel).Namespace};
                 using {typeof(KernelCommand).Namespace};
                 await Kernel.Root.SendAsync(new SubmitCode("error", "cs2"));

                 """);

            await kernel.SendAsync(command);

            kernelEvents.Should()
                        .ContainSingle<ErrorProduced>()
                        .Which
                        .Message
                        .Should()
                        .Be("(1,1): error CS0103: The name 'error' does not exist in the current context");
        }

        [Fact]
        public async Task When_kernel_is_running_inside_another_kernel_its_KernelEvents_are_published()
        {
            var kernel = new CSharpKernel();

            await kernel.SendAsync(new SubmitCode(
                """
                using Microsoft.DotNet.Interactive;
                using Microsoft.DotNet.Interactive.Commands;
                using Microsoft.DotNet.Interactive.Events;
                using Microsoft.DotNet.Interactive.CSharp;
                
                var kernel = new CSharpKernel();
                
                var innerEvents = new List<KernelEvent>();
                
                kernel.KernelEvents.Subscribe(innerEvents.Add);
                
                await kernel.SendAsync(new SubmitCode("123"));
                """));

            kernel.TryGetValue("innerEvents", out List<KernelEvent> innerEvents);

            innerEvents.Should().ContainSingle<ReturnValueProduced>()
                       .Which
                       .Value
                       .Should().Be("123");
        }
    }
}