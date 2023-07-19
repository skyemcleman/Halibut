using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Halibut.Tests.Support;
using Halibut.Tests.Support.TestAttributes;
using Halibut.Tests.Util;
using Halibut.TestUtils.Contracts;
using NUnit.Framework;
using Octopus.TestPortForwarder;

namespace Halibut.Tests.Timeouts
{
    /// <summary>
    /// Where handshake means early on in setting up a TCP connection.
    /// </summary>
    public class TimeoutsApplyDuringHandShake : BaseTest
    {
        [Test]
        public async Task WhenTheFirstWriteOverTheWireOccursOnAConnectionThatImmediatelyPauses_AWriteTimeoutShouldApply(
            [ValuesOfType(typeof(ServiceConnectionTypesToTestExcludingWebSockets))] ServiceConnectionType serviceConnectionType,
            [Values(true, false)] bool onClientToOrigin, // Don't dewll on what this means, we just want to test all combinations of where the timeout can occur.
            [Values(1, 2)]int writeNumberToPauseOn // Ie pause on the first or second write
            ) 
        {
            bool hasPausedAConnection = false;
            int numberOfWritesSeen = 0;
            
            var dataTransferObserverPauser = new DataTransferObserverBuilder()
                .WithWritingDataObserver((tcpPump, Stream) =>
                {

                    Interlocked.Increment(ref numberOfWritesSeen);
                    if (!hasPausedAConnection && numberOfWritesSeen == writeNumberToPauseOn)
                    {
                        hasPausedAConnection = true;
                        Logger.Information("Pausing pump");
                        tcpPump.Pause();
                    }
                })
                .Build();
            var dataTransferObserverDoNothing = new DataTransferObserverBuilder().Build();
            
            using (var clientAndService = await LatestClientAndLatestServiceBuilder
                       .ForServiceConnectionType(serviceConnectionType)
                       .WithPortForwarding(port => PortForwarderUtil.ForwardingToLocalPort(port)
                           .WithDataObserver(() =>
                           {
                               if(onClientToOrigin) return new BiDirectionalDataTransferObserver(dataTransferObserverPauser,dataTransferObserverDoNothing);
                               else return new BiDirectionalDataTransferObserver(dataTransferObserverDoNothing, dataTransferObserverPauser);
                           })
                           .Build())
                       .WithPendingRequestQueueFactory(logFactory => new FuncPendingRequestQueueFactory(uri => new PendingRequestQueue(logFactory.ForEndpoint(uri), TimeSpan.FromSeconds(1))))
                       .WithEchoService()
                       .Build(CancellationToken))
            {
                var echo = clientAndService.CreateClient<IEchoService>(IncreasePollingQueueTimeout());
                var sw = Stopwatch.StartNew();
                try
                {
                    echo.SayHello("Make a request to make sure the connection is running, and ready. Lets not measure SSL setup cost.");
                }
                catch (Exception e)
                {
                    Logger.Information(e, "An exception was raised during the request, this is not an issue since we are concerned about the timings. " +
                                          "Exceptions only occur occasionally and probably only on listening.");
                }

                sw.Stop();
                sw.Elapsed.Should().BeCloseTo(HalibutLimits.TcpClientReceiveTimeout, TimeSpan.FromSeconds(15), "Since a paused connection early on should not hang forever.");

                echo.SayHello("The pump wont be paused here so this should work.");
            }
        }
        
        static Action<ServiceEndPoint> IncreasePollingQueueTimeout()
        {
            return point =>
            {
                // We don't want to measure the polling queue timeouts.
                point.PollingRequestMaximumMessageProcessingTimeout = TimeSpan.FromMinutes(10);
                point.PollingRequestQueueTimeout = TimeSpan.FromMinutes(10);
            };
        }
    }
}