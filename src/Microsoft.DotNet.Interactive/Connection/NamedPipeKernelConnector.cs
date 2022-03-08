﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Tasks;

#nullable enable

namespace Microsoft.DotNet.Interactive.Connection;

public class NamedPipeKernelConnector : IKernelConnector, IDisposable
{
    private MultiplexingKernelCommandAndEventReceiver? _receiver;
    private KernelCommandAndEventPipeStreamSender? _sender;
    private NamedPipeClientStream? _clientStream;

    public string PipeName { get; }
    public async Task<Kernel> ConnectKernelAsync(string kernelName)
    {
        ProxyKernel? proxyKernel;

        if (_receiver is not null)
        {
            proxyKernel = new ProxyKernel(
                kernelName,
                _receiver.CreateChildReceiver(), 
                _sender);
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
            _sender = new KernelCommandAndEventPipeStreamSender(_clientStream);
        
            proxyKernel = new ProxyKernel(kernelName, _receiver, _sender);
        }

        proxyKernel.EnsureStarted();

        return proxyKernel;
    }
        
    public NamedPipeKernelConnector(string pipeName)
    {
        PipeName = pipeName;
    }

    public void Dispose()
    {
        _receiver?.Dispose();
        _clientStream?.Dispose();
    }
}