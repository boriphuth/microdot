﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

using Gigya.Common.Contracts.Exceptions;
using Gigya.Microdot.Fakes;
using Gigya.Microdot.Interfaces.Logging;
using Gigya.Microdot.ServiceDiscovery;
using Gigya.Microdot.ServiceDiscovery.HostManagement;
using Gigya.Microdot.SharedLogic.Monitor;
using Gigya.Microdot.Testing;

using Metrics;

using Ninject;

using NUnit.Framework;

using Shouldly;

namespace Gigya.Microdot.UnitTests.Discovery
{
    [TestFixture]
    public class RemoteHostPoolTests
    {
        private const string SERVICE_NAME = "ServiceName";

        private readonly string serviceContext = $"{SERVICE_NAME}-prod";

        private RemoteHostPool Pool { get; set; }

        private LogSpy Log { get; set; }

        private HealthMonitor _healthMonitor;

        private DiscoverySourceMock _discoverySourceMock { get; set; }

        private void CreatePool(string endPoints, ReachabilityChecker isReachableChecker = null)
        {
            _healthMonitor?.Dispose();
            _healthMonitor = new HealthMonitor();

            var unitTesting = new TestingKernel<LogSpy>(mockConfig: new Dictionary<string, string>  {
                {$"Discovery.Services.{SERVICE_NAME}.DelayMultiplier", "1"},
                {$"Discovery.Services.{SERVICE_NAME}.FirstAttemptDelaySeconds", "0.01"}
            });
            Log = (LogSpy)unitTesting.Get<ILog>();
            var factory = unitTesting.Get<IRemoteHostPoolFactory>();
            _discoverySourceMock = new DiscoverySourceMock(serviceContext, endPoints);
            
            Pool = factory.Create(
                new ServiceDeployment(SERVICE_NAME, "prod"), 
                _discoverySourceMock, 
                isReachableChecker ?? (rh => Task.FromResult(false)));

        }

        private void ChangeConfig(string endPoints)
        {
            _discoverySourceMock.SetEndPoints(endPoints);
        }

        private HealthCheckResult GetHealthResult()
        {
            return HealthChecks.GetStatus().Results.First(o => o.Name == serviceContext).Check;
        }

        private Dictionary<string, string> GetHealthData()
        {
            return _healthMonitor.GetData(serviceContext);
        }

        [TearDown]
        public void TearDown()
        {
            _healthMonitor?.Dispose();
            Pool?.Dispose();
            Pool = null;
        }


        [Test]
        public void GetNextHost_ThreeHosts_ReturnsAllThree()
        {
            CreatePool("host1, host2, host3");

            var allEndpoints = Get100HostNames();

            new[] { "host1", "host2", "host3" }.ShouldBeSubsetOf(allEndpoints);
        }

        [Test]
        public void ThreeHosts_ShouldBeHealthy()
        {
            CreatePool("host1, host2, host3");
            Pool.GetNextHost();
            GetHealthResult().IsHealthy.ShouldBeTrue();

            var healthData = GetHealthData();
            var healthDataReachableHosts = healthData["ReachableHosts"];
            healthDataReachableHosts.ShouldContain("host1");
            healthDataReachableHosts.ShouldContain("host2");
            healthDataReachableHosts.ShouldContain("host3");
            healthData["UnreachableHosts"].ShouldBe(string.Empty);
        }

        [Test]
        public void GetNextHost_HostsThatChange_ReturnsNewHosts()
        {
            CreatePool("host1, host2, host3");
            ChangeConfig("host4, host5, host6");

            var res = Get100HostNames();
            res.Distinct()
               .ShouldBe(new[] { "host4", "host5", "host6" }, true);
        }


        List<string> Get100HostNames()
        {
            List<string> hostNames = new List<string>();

            for (int i = 0; i < 100; i++)
            {
                var host = Pool.GetNextHost();
                hostNames.Add(host.HostName);
            }
            return hostNames;
        }


        [Test]
        public void GetNextHost_LocalhostFallbackOff_NoEndpoints_Throws()
        {
            Should.Throw<EnvironmentException>(() =>
                                               {
                                                   CreatePool("");
                                                   Pool.GetNextHost();
                                               });
        }

        [Test]
        public void GetNextHost_LocalhostFallbackOff_AfterRefreshNoEndpoints_Throws()
        {
            CreatePool("host1, host2, host3");
            ChangeConfig("");
            Should.Throw<EnvironmentException>(() => Pool.GetNextHost());
        }

        [Test]
        public void GetNextHost_LocalhostFallbackOff_AfterRefreshNoEndpoints_ShouldNotBeHealthy()
        {
            CreatePool("host1, host2, host3");
            Pool.GetNextHost();
            ChangeConfig("");
            Should.Throw<EnvironmentException>(() => Pool.GetNextHost());
            var healthResult = GetHealthResult();
            healthResult.IsHealthy.ShouldBeFalse();
            healthResult.Message.ShouldContain("No endpoints were discovered");
        }

        [Test]
        public void GetNextHost_ThreeHosts_BeginsRoundRobinInRandomPosition()
        {
            CreatePool("host1, host2, host3");
            var res = Get100HostNames();
            new[] { "host1", "host2", "host3" }.ShouldBeSubsetOf(res);
        }


        [Test]
        public void ReportFailure_HostFails_WillNotBeReturned()
        {
            var expected = new List<string> { "host1", "host2", "host3" };

            CreatePool(string.Join(",", expected));
            var unreachableHosts = Pool.GetNextHost();
            unreachableHosts.ReportFailure();
            unreachableHosts.ReportFailure();

            expected.Remove(unreachableHosts.HostName);

            for (int i = 0; i < 3; i++)
            {
                expected.ShouldContain(Pool.GetNextHost().HostName);
            }

        }


        [Test]
        public void ReportFailure_OnlyOneHostFails_ShouldStillBeHealthy()
        {
            CreatePool("host1, host2, host3");

            Run100times(host =>
            {
                if (host.HostName == "host2")
                    host.ReportFailure();
            });

            var healthResult = GetHealthResult();
            healthResult.IsHealthy.ShouldBeFalse();
        }


        private void Run100times(Action<RemoteHost> act)
        {
            for (int i = 0; i < 100; i++)
            {
                var host = Pool.GetNextHost();
                act(host);
            }
        }

        [Test]
        public async Task ReportFailure_HostFailsThenReturnsInBackground_WillBeReturned_AndRaiseReachabilityMessage()
        {
            var isReachable = false;
            CreatePool("host2", host => Task.FromResult(isReachable));

            (await Pool.ReachabilitySource.ShouldRaiseMessage(() =>
                {
                    for (int i = 0; i < 2; i++)
                    {
                        var host = Pool.GetNextHost();

                        if (host.HostName == "host2")
                            host.ReportFailure();
                    }
                })).Message.IsReachable.ShouldBeFalse();

            (await Pool.ReachabilitySource.ShouldRaiseMessage(() =>
                {
                    isReachable = true;
                    var healthy = false;
                    while (!healthy)
                    {
                        try
                        {
                            var coolHost = Pool.GetNextHost();
                            coolHost.HostName.ShouldBe("host2");
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        healthy = true;
                    }
                })).Message.IsReachable.ShouldBeTrue();

        }

        [Test]
        public void ReportFailure_AllHostFails_Throws()
        {
            CreatePool("host1, host2, host3", a => Task.FromResult(false));

            for (int i = 0; i < 6; i++)
            {
                var host = Pool.GetNextHost();
                host.ReportFailure();
            }

            Should.Throw<EnvironmentException>(() => Pool.GetNextHost());
        }

        [Test]    
        public void ReportFailure_AllHostFails_ShouldNotBeHealthy()
        {
            CreatePool("host1, host2, host3");

            //two time Failure = unreachable host
            for (int i = 0; i < 6; i++)
            {
                var host = Pool.GetNextHost();
                host.ReportFailure();
            }

            var healthResult = GetHealthResult();
            healthResult.IsHealthy.ShouldBeFalse(healthResult.Message);
            healthResult.Message.ShouldContain("All of the 3 hosts are unreachable");

            var healthData = GetHealthData();
            var healthDataUnreachableHosts = healthData["UnreachableHosts"];
            healthDataUnreachableHosts.ShouldContain("host1");
            healthDataUnreachableHosts.ShouldContain("host2");
            healthDataUnreachableHosts.ShouldContain("host3");
            healthData["ReachableHosts"].ShouldBe(string.Empty);
            HealthChecks.UnregisterAllHealthChecks();
        }


        [Test]
        public void ReportFailure_AllHostFailsThenAllMarkedAsReachable_ReturnsAllHosts()
        {
            CreatePool("host1, host2, host3", rh => Task.FromResult(true));

            for (int i = 0; i < 6; i++)
            {
                var host = Pool.GetNextHost();
                host.ReportFailure();
            }

            Pool.MarkAllAsReachable();

            var res = Get100HostNames();

            new[] { "host1", "host2", "host3" }.ShouldBeSubsetOf(res);
        }


        [Test]
        public void ReportFailure_HostsFailButReachabilityCheckThrows_ErrorIsLogged()
        {
            var ex = new Exception();
            CreatePool("host1, host2, host3", rh => { throw ex; });

            for (int i = 0; i < 6; i++)
            {
                var host = Pool.GetNextHost();

                if (host.HostName == "host2")
                    host.ReportFailure();
            }

            Thread.Sleep(200);

            Pool.Dispose();

            Log.LogEntries.ToArray().ShouldContain(e => e.Severity == TraceEventType.Error && e.Exception == ex);
        }

    }

    internal class DiscoverySourceMock : ServiceDiscoverySourceBase
    {

        public DiscoverySourceMock(string deploymentName, string initialEndPoints) : base(deploymentName)
        {
            EndPoints = GetEndPointsInitialValue(initialEndPoints);
        }

        public void SetEndPoints(string endPoints)
        {
            EndPoints = new EndPoint[0];
            if (!string.IsNullOrWhiteSpace(endPoints))
                EndPoints = endPoints.Split(',').Select(_ => _.Trim())
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(_ => new EndPoint { HostName = _ })
                    .ToArray();

            EndPointsChanged.Post(new EndPointsResult { EndPoints=EndPoints});
            Task.Delay(100).Wait();
        }

        private EndPoint[] GetEndPointsInitialValue(string initialEndPoints)
        {
            if (!string.IsNullOrWhiteSpace(initialEndPoints))
                return initialEndPoints.Split(',').Select(_ => _.Trim())
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(_ => new EndPoint { HostName = _ })
                    .ToArray();

            return new EndPoint[0];
        }

        public bool AlwaysThrowException;

        public override bool IsServiceDeploymentDefined { get; } = true;
        public override Exception AllEndpointsUnreachable(EndPointsResult endpointsResult, Exception lastException, string lastExceptionEndPoint, string unreachableHosts)
        {
            return new EnvironmentException("All endpoints unreachable");
        }
    }
}
