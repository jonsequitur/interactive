﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Connection;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.Server;
using Pocket;

namespace Microsoft.DotNet.Interactive.Tests.Connection;

public class BlockingCommandAndEventReceiver : KernelCommandAndEventReceiverBase
{
    private readonly BlockingCollection<CommandOrEvent> _commandsOrEvents;
    private static readonly Logger<BlockingCommandAndEventReceiver> _log = new();

    public BlockingCommandAndEventReceiver()
    {
        _commandsOrEvents = new BlockingCollection<CommandOrEvent>();
    }

    public IKernelCommandAndEventSender CreateSender()
    {
        return new Sender(this);
    }

    public void Write(CommandOrEvent commandOrEvent)
    {
        using var op = _log.OnEnterAndExit();
        _log.Trace(nameof(commandOrEvent), commandOrEvent);

        if (commandOrEvent.Command is { })
        {
            var command = new CommandOrEvent(
                RoundTripSerializeCommand(commandOrEvent).Command);

            _commandsOrEvents.Add(command);
        }
        else if (commandOrEvent.Event is { })
        {
            var @event = new CommandOrEvent(
                RoundTripSerializeEvent(commandOrEvent).Event);

            _commandsOrEvents.Add(@event);
        }

        static IKernelEventEnvelope RoundTripSerializeEvent(CommandOrEvent commandOrEvent) =>
            KernelEventEnvelope.Deserialize(
                KernelEventEnvelope.Serialize(
                    commandOrEvent.Event));

        static IKernelCommandEnvelope RoundTripSerializeCommand(CommandOrEvent commandOrEvent) =>
            KernelCommandEnvelope.Deserialize(
                KernelCommandEnvelope.Serialize(
                    commandOrEvent.Command));
    }

    protected override async Task<CommandOrEvent> ReadCommandOrEventAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        var commandOrEvent = _commandsOrEvents.Take(cancellationToken);
        return commandOrEvent;
    }

    public class Sender : IKernelCommandAndEventSender
    {
        private readonly BlockingCommandAndEventReceiver _receiver;

        public Sender(BlockingCommandAndEventReceiver receiver)
        {
            _receiver = receiver;
        }

        public Task SendAsync(KernelCommand kernelCommand, CancellationToken cancellationToken)
        {
            using var op = _log.OnEnterAndExit();
            _log.Trace(nameof(kernelCommand), kernelCommand);

            _receiver.Write(new CommandOrEvent(kernelCommand));

            return Task.CompletedTask;
        }

        public Task SendAsync(KernelEvent kernelEvent, CancellationToken cancellationToken)
        {
            using var op = _log.OnEnterAndExit();
            _log.Trace(nameof(kernelEvent), kernelEvent);

            _receiver.Write(new CommandOrEvent(kernelEvent));

            return Task.CompletedTask;
        }
    }
}