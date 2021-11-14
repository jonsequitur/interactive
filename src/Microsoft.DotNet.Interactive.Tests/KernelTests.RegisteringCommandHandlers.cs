﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.Server;
using Microsoft.DotNet.Interactive.Tests.Utility;
using Xunit;

namespace Microsoft.DotNet.Interactive.Tests;

public partial class KernelTests
{
    public class RegisteringCommandHandlers
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void When_command_type_registered_then_kernel_registers_envelope_type_for_serialization(bool withHandler)
        {
            KernelCommandEnvelope.ResetToDefault();

            using var kernel = new FakeKernel();

            if (withHandler)
            {
                kernel.RegisterCommandHandler<CustomCommandTypes.FirstSubmission.MyCommand>(
                    (_, _) => Task.CompletedTask);
            }
            else
            {
                kernel.RegisterCommandType<CustomCommandTypes.FirstSubmission.MyCommand>();
            }

            var originalCommand = new CustomCommandTypes.FirstSubmission.MyCommand("xyzzy");
            string envelopeJson = KernelCommandEnvelope.Serialize(originalCommand);
            var roundTrippedCommandEnvelope = KernelCommandEnvelope.Deserialize(envelopeJson);

            roundTrippedCommandEnvelope
                .Command
                .Should()
                .BeOfType<CustomCommandTypes.FirstSubmission.MyCommand>()
                .Which
                .Info
                .Should()
                .Be(originalCommand.Info);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void When_command_type_reregistered_with_changed_type_command_then_kernel_registers_updated_envelope_type_for_serialization(bool withHandler)
        {
            // Notebook authors should be able to develop their custom commands experimentally and progressively,
            // so we don't want any "you have to restart your kernel now" situations just because you already
            // called RegisterCommandHandler once for a particular command type.
            KernelCommandEnvelope.ResetToDefault();

            using var kernel = new FakeKernel();

            if (withHandler)
            {
                kernel.RegisterCommandHandler<CustomCommandTypes.FirstSubmission.MyCommand>(
                    (_, _) => Task.CompletedTask);
                kernel.RegisterCommandHandler<CustomCommandTypes.SecondSubmission.MyCommand>(
                    (_, _) => Task.CompletedTask);
            }
            else
            {
                kernel.RegisterCommandType<CustomCommandTypes.FirstSubmission.MyCommand>();
                kernel.RegisterCommandType<CustomCommandTypes.SecondSubmission.MyCommand>();
            }

            var originalCommand = new CustomCommandTypes.SecondSubmission.MyCommand("xyzzy", 42);
            string envelopeJson = KernelCommandEnvelope.Serialize(originalCommand);
            var roundTrippedCommandEnvelope = KernelCommandEnvelope.Deserialize(envelopeJson);

            roundTrippedCommandEnvelope
                .Command
                .Should()
                .BeOfType<CustomCommandTypes.SecondSubmission.MyCommand>()
                .Which
                .Info
                .Should()
                .Be(originalCommand.Info);
            roundTrippedCommandEnvelope
                .Command
                .As<CustomCommandTypes.SecondSubmission.MyCommand>()
                .AdditionalProperty
                .Should()
                .Be(originalCommand.AdditionalProperty);
        }

        [Fact]
        public void When_attempting_to_register_a_handler_for_an_implemented_handler_it_throws()
        {
            using var csharpKernel = new CSharpKernel();

            Action register = () =>
                csharpKernel.RegisterCommandHandler<SubmitCode>((_, _) => Task.CompletedTask);

            register.Should()
                    .Throw<InvalidOperationException>()
                    .Which
                    .Message
                    .Should()
                    .Be($"Command {typeof(SubmitCode)} is already directly implemented and registration of an alternative handler is not supported.");
        }

        [Fact]
        public async Task Kernel_info_returns_the_list_of_supported_kernel_commands()
        {
            using var kernel = new FakeKernel();
            kernel.RegisterCommandHandler<RequestHoverText>((_, _) => Task.CompletedTask);
            kernel.RegisterCommandHandler<RequestDiagnostics>((_, _) => Task.CompletedTask);
            kernel.RegisterCommandHandler<CustomCommandTypes.FirstSubmission.MyCommand>((_, _) => Task.CompletedTask);

            var result = await kernel.SendAsync(new RequestKernelInfo());

            var events = result.KernelEvents.ToSubscribedList();

            events.Should()
                  .ContainSingle<KernelInfoProduced>()
                  .Which
                  .KernelInfo
                  .CommandNames
                  .Should()
                  .BeEquivalentTo(
                      nameof(RequestHoverText),
                      nameof(RequestDiagnostics),
                      nameof(SubmitCode),
                      nameof(CustomCommandTypes.FirstSubmission.MyCommand));
        }
    }
}
