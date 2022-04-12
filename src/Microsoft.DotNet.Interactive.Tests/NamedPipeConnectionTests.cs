﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Threading.Tasks;

using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Connection;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive.FSharp;

using Xunit.Abstractions;

namespace Microsoft.DotNet.Interactive.Tests;

// FIX: (NamedPipeConnectionTests) temporarily hiding these tests
internal class NamedPipeConnectionTests : ProxyKernelConnectionTestsBase
{
    private readonly string _pipeName = Guid.NewGuid().ToString();
    private Uri _remoteHostUri;

    public NamedPipeConnectionTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override async Task<IKernelConnector> CreateConnectorAsync()
    {
        await CreateRemoteKernelTopologyAsync(_pipeName);

        var connector = new NamedPipeKernelConnector(_pipeName);

        _remoteHostUri = connector.RemoteHostUri;

        return connector;
    }

    protected override SubmitCode CreateConnectCommand(string localKernelName)
    {
        return new SubmitCode($"#!connect named-pipe --kernel-name {localKernelName} --pipe-name {_pipeName}");
    }

    protected override void AddKernelConnector(CompositeKernel compositeKernel)
    {
        compositeKernel.AddKernelConnector(new ConnectNamedPipeCommand());
    }

    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Test only enabled on windows platforms")]
    private Task<IDisposable> CreateRemoteKernelTopologyAsync(string pipeName)
    {
        var remoteCompositeKernel = new CompositeKernel
        {
            new CSharpKernel(),
            new FSharpKernel()
        };

        remoteCompositeKernel.DefaultKernelName = "csharp";

        RegisterForDisposal(remoteCompositeKernel);

        var serverStream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous);

        var kernelCommandAndEventPipeStreamReceiver = new KernelCommandAndEventPipeStreamReceiver(serverStream);

        var sender = new KernelCommandAndEventPipeStreamSender(
            serverStream,
            new Uri("kernel://remote"));

        var receiver = new MultiplexingKernelCommandAndEventReceiver
(kernelCommandAndEventPipeStreamReceiver);

        var host = remoteCompositeKernel.UseHost(sender, receiver, new Uri("kernel://local"));

        Task.Run(() =>
        {
            // required as waiting connection on named pipe server will block
            serverStream.WaitForConnection();
            var _ = host.ConnectAsync();
        });

        RegisterForDisposal(host);
        RegisterForDisposal(serverStream);

        return Task.FromResult<IDisposable>(host);
    }
}