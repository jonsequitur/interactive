﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Connection;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.Server;

namespace Microsoft.DotNet.Interactive
{
    public class KernelHost : IDisposable
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new ();
        private readonly CompositeKernel _kernel;
        private readonly IKernelCommandAndEventSender _defaultSender;
        private readonly MultiplexingKernelCommandAndEventReceiver _defaultReceiver;
        private Task<Task> _runningLoop;
        private IDisposable _kernelEventSubscription;
        private readonly Dictionary<Kernel, KernelInfo> _kernelInfos = new();
        private readonly Dictionary<Uri, Kernel> _destinationUriToKernel = new();
        private readonly IKernelConnector _defaultConnector;
        private readonly Dictionary<Uri, Kernel> _originUriToKernel = new();

        internal KernelHost(
            CompositeKernel kernel,
            IKernelCommandAndEventSender defaultSender,
            MultiplexingKernelCommandAndEventReceiver defaultReceiver,
            Uri hostUri = null)
        {
            Uri = hostUri ?? new Uri("kernel://dotnet", UriKind.Absolute);
            _kernel = kernel;
            _defaultSender = defaultSender;
            _defaultReceiver = defaultReceiver;
            _defaultConnector = new DefaultKernelConnector(_defaultSender, _defaultReceiver);
            _kernel.SetHost(this);
        }

        private class DefaultKernelConnector : IKernelConnector
        {
            private readonly IKernelCommandAndEventSender _defaultSender;
            private readonly MultiplexingKernelCommandAndEventReceiver _defaultReceiver;

            public DefaultKernelConnector(IKernelCommandAndEventSender defaultSender, MultiplexingKernelCommandAndEventReceiver defaultReceiver)
            {
                _defaultSender = defaultSender;
                _defaultReceiver = defaultReceiver;
            }

            public Task<Kernel> ConnectKernelAsync(KernelInfo kernelInfo)
            {
                var proxy = new ProxyKernel(
                    kernelInfo.LocalName, 
                    _defaultReceiver.CreateChildReceiver(), 
                    _defaultSender);
                
                proxy.Start();
                
                return Task.FromResult<Kernel>(proxy);
            }
        }

        public async Task ConnectAsync()
        {
            if (_runningLoop is { })
            {
                throw new InvalidOperationException("The host is already connected.");
            }

            _kernelEventSubscription = _kernel.KernelEvents.Subscribe(e =>
            {
                if (e is ReturnValueProduced { Value: DisplayedValue })
                {
                    return;
                }
                var _ = _defaultSender.SendAsync(e, _cancellationTokenSource.Token);
            });

            _runningLoop = Task.Factory.StartNew(async () =>
            {
                await foreach (var commandOrEvent in _defaultReceiver.CommandsAndEventsAsync(_cancellationTokenSource.Token))
                {
                    if (commandOrEvent.IsParseError)
                    {
                        var _ = _defaultSender.SendAsync(commandOrEvent.Event, _cancellationTokenSource.Token);
                    }
                    else if (commandOrEvent.Command is { })
                    {
                        var _ = _kernel.SendAsync(commandOrEvent.Command, _cancellationTokenSource.Token);
                    }
                }
            }, _cancellationTokenSource.Token, 
                                                 TaskCreationOptions.LongRunning, TaskScheduler.Default);

            await _defaultSender.NotifyIsReadyAsync(_cancellationTokenSource.Token);
        }

        public async Task ConnectAndWaitAsync()
        {
            await ConnectAsync();
            await _runningLoop;
        }

        public void Dispose()
        {
            _kernelEventSubscription?.Dispose();

            if (_cancellationTokenSource.Token.CanBeCanceled)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
            }
        }

        public bool TryGetKernelInfo(Kernel kernel, out KernelInfo kernelInfo)
        {
            return _kernelInfos.TryGetValue(kernel, out kernelInfo);
        }

        internal void AddKernelInfo(Kernel kernel, KernelInfo kernelInfo)
        {
            kernelInfo.OriginUri = new Uri(Uri, kernel.Name);
            _kernelInfos.Add(kernel,kernelInfo);
            _originUriToKernel[kernelInfo.OriginUri] = kernel;
        }

        public Uri Uri { get;  }


        internal void RegisterDestinationUriForProxy(ProxyKernel proxyKernel, Uri destinationUri)
        {
            if (proxyKernel == null)
            {
                throw new ArgumentNullException(nameof(proxyKernel));
            }

            if (destinationUri == null)
            {
                throw new ArgumentNullException(nameof(destinationUri));
            }

            if (TryGetKernelInfo(proxyKernel, out var kernelInfo))
            {
                if (kernelInfo.DestinationUri is { })
                {
                    _destinationUriToKernel.Remove(kernelInfo.DestinationUri);
                }

                kernelInfo.DestinationUri = destinationUri;
                _destinationUriToKernel[kernelInfo.DestinationUri] = proxyKernel;
            }
            else
            {
                throw new ArgumentException($"Unknown kernel name : {proxyKernel.Name}");
            }
        }

        internal void RegisterDestinationUriForProxy(string proxyLocalKernelName, Uri destinationUri)
        {
            var childKernel = _kernel.FindKernel(proxyLocalKernelName);
            if (childKernel is ProxyKernel proxyKernel)
            {
                RegisterDestinationUriForProxy(proxyKernel, destinationUri);
            }
            else
            {
                throw new ArgumentException($"Cannot find Kernel {proxyLocalKernelName} or it is not a valid ProxyKernel");
            }
        }

        public async Task<ProxyKernel> CreateProxyKernelOnDefaultConnectorAsync(KernelInfo kernelInfo)
        {
            var childKernel = await CreateProxyKernelOnConnectorAsync(kernelInfo,_defaultConnector);
            return childKernel;
        }

        public async Task<ProxyKernel> CreateProxyKernelOnConnectorAsync(KernelInfo kernelInfo, IKernelConnector kernelConnector )
        {
            var childKernel = await kernelConnector.ConnectKernelAsync(kernelInfo) as ProxyKernel;
            _kernel.Add(childKernel, kernelInfo.Aliases);
            RegisterDestinationUriForProxy(kernelInfo.LocalName, kernelInfo.DestinationUri);
            return childKernel;
        }

        public bool TryGetKernelByDestinationUri(Uri destinationUri, out Kernel kernel)
        {
            return _destinationUriToKernel.TryGetValue(destinationUri, out kernel);
        }

        public bool TryGetKernelByOriginUri(Uri originUri, out Kernel kernel)
        {
            return _originUriToKernel.TryGetValue(originUri, out kernel);
        }
    }
}

