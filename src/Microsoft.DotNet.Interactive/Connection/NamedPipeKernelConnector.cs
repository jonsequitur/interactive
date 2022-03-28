﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.Interactive.Commands;

#nullable enable

namespace Microsoft.DotNet.Interactive.Connection;

public class NamedPipeKernelConnector : IKernelConnector, IDisposable
{
    private MultiplexingKernelCommandAndEventReceiver? _receiver;
    private KernelCommandAndEventPipeStreamSender? _sender;
    private NamedPipeClientStream? _clientStream;

    public NamedPipeKernelConnector(string pipeName)
    {
        PipeName = pipeName;
        RemoteHostUri = new Uri($"kernel://{PipeName}");
    }

    public string PipeName { get; }

    public Uri RemoteHostUri { get; }

    public async Task<Kernel> CreateKernelAsync(string localName)
    {
        ProxyKernel? proxyKernel;

        if (_receiver is not null)
        {
            proxyKernel = new ProxyKernel(
                localName,
                _receiver.CreateChildReceiver(),
                _sender,
                new Uri(RemoteHostUri, localName));
        }
        else
        {
            _clientStream = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Impersonation);

            await _clientStream.ConnectAsync();

            _clientStream.ReadMode = PipeTransmissionMode.Message;

            _receiver = new MultiplexingKernelCommandAndEventReceiver(new KernelCommandAndEventPipeStreamReceiver(_clientStream));
            _sender = new KernelCommandAndEventPipeStreamSender(
                _clientStream, 
                RemoteHostUri);

            proxyKernel = new ProxyKernel(localName, _receiver, _sender, new Uri(RemoteHostUri, localName));
        }
        
        var destinationUri = new Uri(RemoteHostUri, localName);

        await _sender!.SendAsync(
            new RequestKernelInfo(destinationUri: destinationUri), 
            CancellationToken.None);
        
        proxyKernel.EnsureStarted();

        return proxyKernel;
    }

    public void Dispose()
    {
        _receiver?.Dispose();
        _clientStream?.Dispose();
    }
}