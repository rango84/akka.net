﻿//-----------------------------------------------------------------------
// <copyright file="EndpointManager.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Dispatch;
using Akka.Event;
using Akka.Remote.Transport;
using Akka.Util.Internal;

namespace Akka.Remote
{
    /// <summary>
    /// INTERNAL API
    /// </summary>
    internal class EndpointManager : ReceiveActor, IRequiresMessageQueue<IUnboundedMessageQueueSemantics>
    {

        #region Policy definitions

        /// <summary>
        /// TBD
        /// </summary>
        public abstract class EndpointPolicy
        {
            /// <summary>
            /// Indicates that the policy does not contain an active endpoint, but it is a tombstone of a previous failure
            /// </summary>
            public readonly bool IsTombstone;

            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="isTombstone">TBD</param>
            protected EndpointPolicy(bool isTombstone)
            {
                IsTombstone = isTombstone;
            }
        }

        /// <summary>
        /// We will always accept a connection from this remote node.
        /// </summary>
        public sealed class Pass : EndpointPolicy
        {
            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="endpoint">TBD</param>
            /// <param name="uid">TBD</param>
            /// <param name="refuseUid">TBD</param>
            public Pass(IActorRef endpoint, int? uid, int? refuseUid)
                : base(false)
            {
                Uid = uid;
                Endpoint = endpoint;
                RefuseUid = refuseUid;
            }

            /// <summary>
            /// TBD
            /// </summary>
            public IActorRef Endpoint { get; private set; }

            /// <summary>
            /// TBD
            /// </summary>
            public int? Uid { get; private set; }

            /// <summary>
            /// TBD
            /// </summary>
            public int? RefuseUid { get; private set; }
        }

        /// <summary>
        /// A Gated node can't be connected to from this process for <see cref="TimeOfRelease"/>,
        /// but we may accept an inbound connection from it if the remote node recovers on its own.
        /// </summary>
        public sealed class Gated : EndpointPolicy
        {
            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="deadline">TBD</param>
            /// <param name="refuseUid">TBD</param>
            public Gated(Deadline deadline, int? refuseUid)
                : base(true)
            {
                TimeOfRelease = deadline;
                RefuseUid = refuseUid;
            }

            /// <summary>
            /// TBD
            /// </summary>
            public Deadline TimeOfRelease { get; private set; }

            /// <summary>
            /// TBD
            /// </summary>
            public int? RefuseUid { get; private set; }
        }

        /// <summary>
        /// Used to indicated that a node was <see cref="Gated"/> previously.
        /// </summary>
        public sealed class WasGated : EndpointPolicy
        {
            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="refuseUid">TBD</param>
            public WasGated(int? refuseUid) : base(false)
            {
                RefuseUid = refuseUid;
            }

            /// <summary>
            /// TBD
            /// </summary>
            public int? RefuseUid { get; private set; }
        }

        /// <summary>
        /// We do not accept connection attempts for a quarantined node until it restarts and resets its UID.
        /// </summary>
        public sealed class Quarantined : EndpointPolicy
        {
            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="uid">TBD</param>
            /// <param name="deadline">TBD</param>
            public Quarantined(int uid, Deadline deadline)
                : base(true)
            {
                Uid = uid;
                Deadline = deadline;
            }

            /// <summary>
            /// TBD
            /// </summary>
            public int Uid { get; private set; }

            /// <summary>
            /// TBD
            /// </summary>
            public Deadline Deadline { get; private set; }
        }

        #endregion

        #region RemotingCommands and operations

        /// <summary>
        /// Messages sent between <see cref="Remoting"/> and <see cref="EndpointManager"/>
        /// </summary>
        public abstract class RemotingCommand : INoSerializationVerificationNeeded { }

        /// <summary>
        /// TBD
        /// </summary>
        public sealed class Listen : RemotingCommand
        {
            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="addressesPromise">TBD</param>
            public Listen(TaskCompletionSource<IList<ProtocolTransportAddressPair>> addressesPromise)
            {
                AddressesPromise = addressesPromise;
            }

            /// <summary>
            /// TBD
            /// </summary>
            public TaskCompletionSource<IList<ProtocolTransportAddressPair>> AddressesPromise { get; private set; }
        }

        /// <summary>
        /// TBD
        /// </summary>
        public sealed class StartupFinished : RemotingCommand { }

        /// <summary>
        /// TBD
        /// </summary>
        public sealed class ShutdownAndFlush : RemotingCommand { }

        /// <summary>
        /// TBD
        /// </summary>
        public sealed class Send : RemotingCommand, IHasSequenceNumber
        {
            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="message">TBD</param>
            /// <param name="recipient">TBD</param>
            /// <param name="senderOption">TBD</param>
            /// <param name="seqOpt">TBD</param>
            public Send(object message, RemoteActorRef recipient, IActorRef senderOption = null, SeqNo seqOpt = null)
            {
                Recipient = recipient;
                SenderOption = senderOption;
                Message = message;
                _seq = seqOpt;
            }

            /// <summary>
            /// TBD
            /// </summary>
            public object Message { get; private set; }

            /// <summary>
            /// Can be null!
            /// </summary>
            public IActorRef SenderOption { get; private set; }

            /// <summary>
            /// TBD
            /// </summary>
            public RemoteActorRef Recipient { get; private set; }

            /// <summary>
            /// TBD
            /// </summary>
            /// <returns>TBD</returns>
            public override string ToString()
            {
                return string.Format("Remote message {0} -> {1}", SenderOption, Recipient);
            }

            private readonly SeqNo _seq;

            /// <summary>
            /// TBD
            /// </summary>
            public SeqNo Seq
            {
                get
                {
                    return _seq;
                }
            }

            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="opt">TBD</param>
            /// <returns>TBD</returns>
            public Send Copy(SeqNo opt)
            {
                return new Send(Message, Recipient, SenderOption, opt);
            }
        }

        /// <summary>
        /// TBD
        /// </summary>
        public sealed class Quarantine : RemotingCommand
        {
            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="remoteAddress">TBD</param>
            /// <param name="uid">TBD</param>
            public Quarantine(Address remoteAddress, int? uid)
            {
                Uid = uid;
                RemoteAddress = remoteAddress;
            }

            /// <summary>
            /// TBD
            /// </summary>
            public Address RemoteAddress { get; private set; }

            /// <summary>
            /// TBD
            /// </summary>
            public int? Uid { get; private set; }
        }

        /// <summary>
        /// TBD
        /// </summary>
        public sealed class ManagementCommand : RemotingCommand
        {
            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="cmd">TBD</param>
            public ManagementCommand(object cmd)
            {
                Cmd = cmd;
            }

            /// <summary>
            /// TBD
            /// </summary>
            public object Cmd { get; private set; }
        }

        /// <summary>
        /// TBD
        /// </summary>
        public sealed class ManagementCommandAck
        {
            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="status">TBD</param>
            public ManagementCommandAck(bool status)
            {
                Status = status;
            }

            /// <summary>
            /// TBD
            /// </summary>
            public bool Status { get; private set; }
        }

        #endregion

        #region Messages internal to EndpointManager

        /// <summary>
        /// TBD
        /// </summary>
        public sealed class Prune : INoSerializationVerificationNeeded { }

        /// <summary>
        /// TBD
        /// </summary>
        public sealed class ListensResult : INoSerializationVerificationNeeded
        {
            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="addressesPromise">TBD</param>
            /// <param name="results">TBD</param>
            public ListensResult(TaskCompletionSource<IList<ProtocolTransportAddressPair>> addressesPromise, List<Tuple<ProtocolTransportAddressPair, TaskCompletionSource<IAssociationEventListener>>> results)
            {
                Results = results;
                AddressesPromise = addressesPromise;
            }

            /// <summary>
            /// TBD
            /// </summary>
            public TaskCompletionSource<IList<ProtocolTransportAddressPair>> AddressesPromise { get; private set; }

            /// <summary>
            /// TBD
            /// </summary>
            public IList<Tuple<ProtocolTransportAddressPair, TaskCompletionSource<IAssociationEventListener>>> Results
            { get; private set; }
        }

        /// <summary>
        /// TBD
        /// </summary>
        public sealed class ListensFailure : INoSerializationVerificationNeeded
        {
            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="addressesPromise">TBD</param>
            /// <param name="cause">TBD</param>
            public ListensFailure(TaskCompletionSource<IList<ProtocolTransportAddressPair>> addressesPromise, Exception cause)
            {
                Cause = cause;
                AddressesPromise = addressesPromise;
            }

            /// <summary>
            /// TBD
            /// </summary>
            public TaskCompletionSource<IList<ProtocolTransportAddressPair>> AddressesPromise { get; private set; }

            /// <summary>
            /// TBD
            /// </summary>
            public Exception Cause { get; private set; }
        }

        /// <summary>
        /// Helper class to store address pairs
        /// </summary>
        public sealed class Link
        {
            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="localAddress">TBD</param>
            /// <param name="remoteAddress">TBD</param>
            public Link(Address localAddress, Address remoteAddress)
            {
                RemoteAddress = remoteAddress;
                LocalAddress = localAddress;
            }

            /// <summary>
            /// TBD
            /// </summary>
            public Address LocalAddress { get; }

            /// <summary>
            /// TBD
            /// </summary>
            public Address RemoteAddress { get; }

            /// <summary>
            /// Overrode this to make sure that the <see cref="ReliableDeliverySupervisor"/> can correctly store
            /// <see cref="AckedReceiveBuffer{T}"/> data for each <see cref="Link"/> individually, since the HashCode
            /// is what Dictionary types use internally for equality checking by default.
            /// </summary>
            /// <returns>TBD</returns>
            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = 17;
                    hash = hash * 23 + (LocalAddress == null ? 0 : LocalAddress.GetHashCode());
                    hash = hash * 23 + (RemoteAddress == null ? 0 : RemoteAddress.GetHashCode());
                    return hash;
                }
            }
        }

        /// <summary>
        /// TBD
        /// </summary>
        public sealed class ResendState
        {
            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="uid">TBD</param>
            /// <param name="buffer">TBD</param>
            public ResendState(int uid, AckedReceiveBuffer<Message> buffer)
            {
                Buffer = buffer;
                Uid = uid;
            }

            /// <summary>
            /// TBD
            /// </summary>
            public int Uid { get; private set; }

            /// <summary>
            /// TBD
            /// </summary>
            public AckedReceiveBuffer<Message> Buffer { get; private set; }
        }

        #endregion

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="config">TBD</param>
        /// <param name="log">TBD</param>
        public EndpointManager(Config config, ILoggingAdapter log)
        {
            _conf = config;
            _settings = new RemoteSettings(_conf);
            _log = log;
            _eventPublisher = new EventPublisher(Context.System, log, Logging.LogLevelFor(_settings.RemoteLifecycleEventsLogLevel));

            Receiving();
        }

        /// <summary>
        /// Mapping between addresses and endpoint actors. If passive connections are turned off, incoming connections
        /// will not be part of this map!
        /// </summary>
        private readonly EndpointRegistry _endpoints = new EndpointRegistry();
        private readonly RemoteSettings _settings;
        private readonly Config _conf;
        private readonly AtomicCounterLong _endpointId = new AtomicCounterLong(0L);
        private readonly ILoggingAdapter _log;
        private readonly EventPublisher _eventPublisher;

        /// <summary>
        /// Used to indicate when an abrupt shutdown occurs
        /// </summary>
        private bool _normalShutdown = false;

        /// <summary>
        /// Mapping between transports and the local addresses they listen to
        /// </summary>
        private Dictionary<Address, AkkaProtocolTransport> _transportMapping =
            new Dictionary<Address, AkkaProtocolTransport>();

        private readonly ConcurrentDictionary<Link, ResendState> _receiveBuffers = new ConcurrentDictionary<Link, ResendState>();

        private bool RetryGateEnabled
        {
            get { return _settings.RetryGateClosedFor > TimeSpan.Zero; }
        }

        private TimeSpan PruneInterval
        {
            get
            {
                //PruneInterval = 2x the RetryGateClosedFor value, if available
                if (RetryGateEnabled) return _settings.RetryGateClosedFor.Add(_settings.RetryGateClosedFor).Max(TimeSpan.FromSeconds(1)).Min(TimeSpan.FromSeconds(10));
                else return TimeSpan.Zero;
            }
        }

        private ICancelable _pruneTimeCancelable;

        /// <summary>
        /// Cancelable for terminating <see cref="Prune"/> operations.
        /// </summary>
        private ICancelable PruneTimerCancelleable
        {
            get
            {
                if (RetryGateEnabled && _pruneTimeCancelable == null)
                {
                    return _pruneTimeCancelable = Context.System.Scheduler.ScheduleTellRepeatedlyCancelable(PruneInterval, PruneInterval, Self, new Prune(), Self);
                }
                return _pruneTimeCancelable;
            }
        }

        private Dictionary<IActorRef, AkkaProtocolHandle> _pendingReadHandoffs = new Dictionary<IActorRef, AkkaProtocolHandle>();
        private Dictionary<IActorRef, List<InboundAssociation>> _stashedInbound = new Dictionary<IActorRef, List<InboundAssociation>>();


        private void HandleStashedInbound(IActorRef endpoint, bool writerIsIdle)
        {
            var stashed = _stashedInbound.GetOrElse(endpoint, new List<InboundAssociation>());
            _stashedInbound.Remove(endpoint);
            foreach (var ia in stashed)
                HandleInboundAssociation(ia, writerIsIdle);
        }

        private void KeepQuarantinedOr(Address remoteAddress, Action body)
        {
            var uid = _endpoints.RefuseUid(remoteAddress);
            if (uid.HasValue)
            {
                _log.Info(
                    "Quarantined address [{0}] is still unreachable or has not been restarted. Keeping it quarantined.",
                    remoteAddress);
                // Restoring Quarantine marker overwritten by a Pass(endpoint, refuseUid) pair while probing remote system.
                _endpoints.MarkAsQuarantined(remoteAddress, uid.Value, Deadline.Now + _settings.QuarantineDuration);
            }
            else
            {
                body();
            }
        }


        #region ActorBase overrides

        /// <summary>
        /// TBD
        /// </summary>
        /// <returns>TBD</returns>
        protected override SupervisorStrategy SupervisorStrategy()
        {
            return new OneForOneStrategy(ex =>
            {
                var directive = Directive.Stop;

                ex.Match()
                    .With<InvalidAssociation>(ia =>
                    {
                        KeepQuarantinedOr(ia.RemoteAddress, () =>
                        {
                            var causedBy = ia.InnerException == null
                                ? ""
                                : $"Caused by: [{ia.InnerException}]";
                            _log.Warning("Tried to associate with unreachable remote address [{0}]. Address is now gated for {1} ms, all messages to this address will be delivered to dead letters. Reason: [{2}] {3}",
                                ia.RemoteAddress, _settings.RetryGateClosedFor.TotalMilliseconds, ia.Message, causedBy);
                            _endpoints.MarkAsFailed(Sender, Deadline.Now + _settings.RetryGateClosedFor);
                        });

                        if (ia.DisassociationInfo.HasValue && ia.DisassociationInfo == DisassociateInfo.Quarantined)
                            Context.System.EventStream.Publish(new ThisActorSystemQuarantinedEvent(ia.LocalAddress, ia.RemoteAddress));

                        directive = Directive.Stop;
                    })
                    .With<ShutDownAssociation>(shutdown =>
                    {
                        KeepQuarantinedOr(shutdown.RemoteAddress, () =>
                        {
                            _log.Debug("Remote system with address [{0}] has shut down. Address is now gated for {1}ms, all messages to this address will be delivered to dead letters.",
                                 shutdown.RemoteAddress, _settings.RetryGateClosedFor.TotalMilliseconds);
                            _endpoints.MarkAsFailed(Sender, Deadline.Now + _settings.RetryGateClosedFor);
                        });
                        directive = Directive.Stop;
                    })
                    .With<HopelessAssociation>(hopeless =>
                    {
                        if (hopeless.Uid.HasValue)
                        {
                            _log.Error(hopeless.InnerException, "Association to [{0}] with UID [{1}] is irrecoverably failed. Quarantining address.",
                                hopeless.RemoteAddress, hopeless.Uid);
                            if (_settings.QuarantineDuration.HasValue)
                            {
                                _endpoints.MarkAsQuarantined(hopeless.RemoteAddress, hopeless.Uid.Value,
                               Deadline.Now + _settings.QuarantineDuration.Value);
                                _eventPublisher.NotifyListeners(new QuarantinedEvent(hopeless.RemoteAddress,
                                    hopeless.Uid.Value));
                            }
                        }
                        else
                        {
                            _log.Warning("Association to [{0}] with unknown UID is irrecoverably failed. Address cannot be quarantined without knowing the UID, gating instead for {1} ms.",
                                hopeless.RemoteAddress, _settings.RetryGateClosedFor.TotalMilliseconds);
                            _endpoints.MarkAsFailed(Sender, Deadline.Now + _settings.RetryGateClosedFor);
                        }
                        directive = Directive.Stop;
                    })
                    .Default(msg =>
                    {
                        if (msg is EndpointDisassociatedException || msg is EndpointAssociationException) { } //no logging
                        else { _log.Error(ex, ex.Message); }
                        _endpoints.MarkAsFailed(Sender, Deadline.Now + _settings.RetryGateClosedFor);
                        directive = Directive.Stop;
                    });

                return directive;
            }, false);
        }

        /// <summary>
        /// TBD
        /// </summary>
        protected override void PreStart()
        {
            if (PruneTimerCancelleable != null)
                _log.Debug("Starting prune timer for endpoint manager...");
            base.PreStart();
        }

        /// <summary>
        /// TBD
        /// </summary>
        protected override void PostStop()
        {
            if (PruneTimerCancelleable != null)
                _pruneTimeCancelable.Cancel();
            foreach (var h in _pendingReadHandoffs.Values)
                h.Disassociate(DisassociateInfo.Shutdown);

            if (!_normalShutdown)
            {
                // Remaining running endpoints are children, so they will clean up themselves.
                // We still need to clean up any remaining transports because handles might be in mailboxes, and for example
                // Netty is not part of the actor hierarchy, so its handles will not be cleaned up if no actor is taking
                // responsibility of them (because they are sitting in a mailbox).
                _log.Error("Remoting system has been terminated abruptly. Attempting to shut down transports");
                foreach (var t in _transportMapping.Values)
                    t.Shutdown();
            }
        }

        private void Receiving()
        {
            /*
            * the first command the EndpointManager receives.
            * instructs the EndpointManager to fire off its "Listens" command, which starts
            * up all inbound transports and binds them to specific addresses via configuration.
            * those results will then be piped back to Remoting, who waits for the results of
            * listen.AddressPromise.
            * */
            Receive<Listen>(listen =>
            {
                Listens.ContinueWith<INoSerializationVerificationNeeded>(listens =>
                {
                    if (listens.IsFaulted)
                    {
                        return new ListensFailure(listen.AddressesPromise, listens.Exception);
                    }
                    else
                    {
                        return new ListensResult(listen.AddressesPromise, listens.Result);
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                    .PipeTo(Self);
            });

            Receive<ListensResult>(listens =>
            {
                _transportMapping = (from mapping in listens.Results
                                     group mapping by mapping.Item1.Address
                                           into g
                                     select new { address = g.Key, transports = g.ToList() }).Select(x =>
                                     {
                                         if (x.transports.Count > 1)
                                         {
                                             throw new RemoteTransportException(
                                                 string.Format("There are more than one transports listening on local address {0}",
                                                     x.address));
                                         }
                                         return new KeyValuePair<Address, AkkaProtocolTransport>(x.address,
                                             x.transports.Head().Item1.ProtocolTransport);
                                     }).ToDictionary(x => x.Key, v => v.Value);

                //Register a listener to each transport and collect mapping to addresses
                var transportsAndAddresses = listens.Results.Select(x =>
                {
                    x.Item2.SetResult(new ActorAssociationEventListener(Self));
                    return x.Item1;
                }).ToList();

                listens.AddressesPromise.SetResult(transportsAndAddresses);
            });
            Receive<ListensFailure>(failure => failure.AddressesPromise.SetException(failure.Cause));

            // defer the inbound association until we can enter "Accepting" behavior

            Receive<InboundAssociation>(
                ia => Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromMilliseconds(10), Self, ia, Self));
            Receive<ManagementCommand>(mc => Sender.Tell(new ManagementCommandAck(status: false)));
            Receive<StartupFinished>(sf => Become(Accepting));
            Receive<ShutdownAndFlush>(sf =>
             {
                 Sender.Tell(true);
                 Context.Stop(Self);
             });
        }

        /// <summary>
        /// Message-processing behavior when the <see cref="EndpointManager"/> is able to accept
        /// inbound association requests.
        /// </summary>
        protected void Accepting()
        {
            Receive<ManagementCommand>(mc =>
                {
                    /*
                     * applies a management command to all available transports.
                     * 
                     * Useful for things like global restart 
                     */
                    var sender = Sender;
                    var allStatuses = _transportMapping.Values.Select(x => x.ManagementCommand(mc.Cmd));
                    Task.WhenAll(allStatuses)
                        .ContinueWith(x =>
                        {
                            return new ManagementCommandAck(x.Result.All(y => y));
                        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                        .PipeTo(sender);
                });

            Receive<Quarantine>(quarantine =>
            {
                //Stop writers
                var policy =
                Tuple.Create(_endpoints.WritableEndpointWithPolicyFor(quarantine.RemoteAddress), quarantine.Uid);
                if (policy.Item1 is Pass && policy.Item2 == null)
                {
                    var endpoint = policy.Item1.AsInstanceOf<Pass>().Endpoint;
                    Context.Stop(endpoint);
                    _log.Warning("Association to [{0}] with unknown UID is reported as quarantined, but " +
                    "address cannot be quarantined without knowing the UID, gating instead for {1} ms.", quarantine.RemoteAddress, _settings.RetryGateClosedFor.TotalMilliseconds);
                    _endpoints.MarkAsFailed(endpoint, Deadline.Now + _settings.RetryGateClosedFor);
                }
                else if (policy.Item1 is Pass && policy.Item2 != null)
                {
                    var pass = (Pass)policy.Item1;
                    var uidOption = pass.Uid;
                    var quarantineUid = policy.Item2;
                    if (uidOption == quarantineUid)
                    {
                        _endpoints.MarkAsQuarantined(quarantine.RemoteAddress, quarantineUid.Value, Deadline.Now + _settings.QuarantineDuration);
                        _eventPublisher.NotifyListeners(new QuarantinedEvent(quarantine.RemoteAddress, quarantineUid.Value));
                        Context.Stop(pass.Endpoint);
                    }
                    // or it does not match with the UID to be quarantined
                    else if (!uidOption.HasValue && pass.RefuseUid != quarantineUid)
                    {
                        // the quarantine uid may be got fresh by cluster gossip, so update refuseUid for late handle when the writer got uid
                        _endpoints.RegisterWritableEndpointRefuseUid(quarantine.RemoteAddress, quarantineUid.Value);
                    }
                    else
                    {
                        //the quarantine uid has lost the race with some failure, do nothing
                    }
                }
                else if (policy.Item1 is WasGated && policy.Item2 != null)
                {
                    var wg = (WasGated)policy.Item1;
                    if (wg.RefuseUid == policy.Item2)
                        _endpoints.RegisterWritableEndpointRefuseUid(quarantine.RemoteAddress, policy.Item2.Value);
                }
                else if (policy.Item1 is Quarantined && policy.Item2 != null && policy.Item1.AsInstanceOf<Quarantined>().Uid == policy.Item2.Value)
                {
                    // the UID to be quarantined already exists, do nothing
                }
                else if (policy.Item2 != null)
                {
                    // the current state is gated or quarantined, and we know the UID, update
                    _endpoints.MarkAsQuarantined(quarantine.RemoteAddress, policy.Item2.Value, Deadline.Now + _settings.QuarantineDuration);
                    _eventPublisher.NotifyListeners(new QuarantinedEvent(quarantine.RemoteAddress, policy.Item2.Value));
                }
                else
                {
                    // the current state is Gated, WasGated, or Quarantined and we don't know the UID, do nothing.
                }

                // Stop inbound read-only associations
                var readPolicy = Tuple.Create(_endpoints.ReadOnlyEndpointFor(quarantine.RemoteAddress), quarantine.Uid);
                if (readPolicy.Item1?.Item1 != null && quarantine.Uid == null)
                    Context.Stop(readPolicy.Item1.Item1);
                else if (readPolicy.Item1?.Item1 != null && quarantine.Uid != null && readPolicy.Item1?.Item2 == quarantine.Uid) { Context.Stop(readPolicy.Item1.Item1); }
                else { } // nothing to stop

                Func<AkkaProtocolHandle, bool> matchesQuarantine = handle => handle.RemoteAddress.Equals(quarantine.RemoteAddress) &&
                                                                         quarantine.Uid == handle.HandshakeInfo.Uid;

                // Stop all matching pending read handoffs
                _pendingReadHandoffs = _pendingReadHandoffs.Where(x =>
                {
                    var drop = matchesQuarantine(x.Value);
                    // Side-effecting here
                    if (drop)
                    {
                        x.Value.Disassociate();
                        Context.Stop(x.Key);
                    }
                    return !drop;
                }).ToDictionary(key => key.Key, value => value.Value);

                // Stop all matching stashed connections
                _stashedInbound = _stashedInbound.Select(x =>
                {
                    var associations = x.Value.Where(assoc =>
                    {
                        var handle = assoc.Association.AsInstanceOf<AkkaProtocolHandle>();
                        var drop = matchesQuarantine(handle);
                        if (drop)
                            handle.Disassociate();
                        return !drop;
                    }).ToList();
                    return new KeyValuePair<IActorRef, List<InboundAssociation>>(x.Key, associations);
                }).ToDictionary(k => k.Key, v => v.Value);

            });

            Receive<Send>(send =>
            {
                var recipientAddress = send.Recipient.Path.Address;
                Func<int?, IActorRef> createAndRegisterWritingEndpoint = refuseUid => _endpoints.RegisterWritableEndpoint(recipientAddress,
                    CreateEndpoint(recipientAddress, send.Recipient.LocalAddressToUse,
                        _transportMapping[send.Recipient.LocalAddressToUse], _settings, writing: true,
                        handleOption: null, refuseUid: refuseUid), uid: null, refuseUid: refuseUid);

                // pattern match won't throw a NullReferenceException if one is returned by WritableEndpointWithPolicyFor
                _endpoints.WritableEndpointWithPolicyFor(recipientAddress).Match()
                    .With<Pass>(
                        pass =>
                        {
                            pass.Endpoint.Tell(send);
                        })
                    .With<Gated>(gated =>
                    {
                        if (gated.TimeOfRelease.IsOverdue) createAndRegisterWritingEndpoint(gated.RefuseUid).Tell(send);
                        else Context.System.DeadLetters.Tell(send);
                    })
                    .With<WasGated>(wasGated =>
                    {
                        createAndRegisterWritingEndpoint(wasGated.RefuseUid).Tell(send);
                    })
                    .With<Quarantined>(quarantined =>
                    {
                        // timeOfRelease is only used for garbage collection reasons, therefore it is ignored here. We still have
                        // the Quarantined tombstone and we know what UID we don't want to accept, so use it.
                        createAndRegisterWritingEndpoint(quarantined.Uid).Tell(send);
                    })
                    .Default(msg => createAndRegisterWritingEndpoint(null).Tell(send));
            });
            Receive<InboundAssociation>(ia => HandleInboundAssociation(ia, false));
            Receive<EndpointWriter.StoppedReading>(endpoint => AcceptPendingReader(endpoint.Writer));
            Receive<Terminated>(terminated =>
            {
                AcceptPendingReader(terminated.ActorRef);
                _endpoints.UnregisterEndpoint(terminated.ActorRef);
                HandleStashedInbound(terminated.ActorRef, writerIsIdle: false);
            });
            Receive<EndpointWriter.TookOver>(tookover => RemovePendingReader(tookover.Writer, tookover.ProtocolHandle));
            Receive<ReliableDeliverySupervisor.GotUid>(gotuid =>
            {

                var policy = _endpoints.WritableEndpointWithPolicyFor(gotuid.RemoteAddress);
                var pass = policy as Pass;
                if (pass != null)
                {
                    if (pass.RefuseUid == gotuid.Uid)
                    {
                        _endpoints.MarkAsQuarantined(gotuid.RemoteAddress, gotuid.Uid,
                            Deadline.Now + _settings.QuarantineDuration);
                        _eventPublisher.NotifyListeners(new QuarantinedEvent(gotuid.RemoteAddress, gotuid.Uid));
                        Context.Stop(pass.Endpoint);
                    }
                    else
                    {
                        _endpoints.RegisterWritableEndpointUid(gotuid.RemoteAddress, gotuid.Uid);
                    }
                    HandleStashedInbound(Sender, writerIsIdle: false);
                }
                else if (policy is WasGated)
                {
                    var wg = (WasGated) policy;
                    if (wg.RefuseUid == gotuid.Uid)
                    {
                        _endpoints.MarkAsQuarantined(gotuid.RemoteAddress, gotuid.Uid,
                            Deadline.Now + _settings.QuarantineDuration);
                        _eventPublisher.NotifyListeners(new QuarantinedEvent(gotuid.RemoteAddress, gotuid.Uid));
                    }
                    else
                    {
                        _endpoints.RegisterWritableEndpointUid(gotuid.RemoteAddress, gotuid.Uid);
                    }
                    HandleStashedInbound(Sender, writerIsIdle: false);
                }
                else
                {
                    // the GotUid might have lost the race with some failure
                }

            });
            Receive<ReliableDeliverySupervisor.Idle>(idle =>
            {
                HandleStashedInbound(Sender, writerIsIdle: true);
            });
            Receive<Prune>(prune => _endpoints.Prune());
            Receive<ShutdownAndFlush>(shutdown =>
            {
                //Shutdown all endpoints and signal to Sender when ready (and whether all endpoints were shutdown gracefully)
                var sender = Sender;

                // The construction of the Task for shutdownStatus has to happen after the flushStatus future has been finished
                // so that endpoints are shut down before transports.
                var shutdownStatus = Task.WhenAll(_endpoints.AllEndpoints.Select(
                    x => x.GracefulStop(_settings.FlushWait, EndpointWriter.FlushAndStop.Instance))).ContinueWith(
                        result =>
                        {
                            if (result.IsFaulted || result.IsCanceled)
                            {
                                if (result.Exception != null)
                                    result.Exception.Handle(e => true);
                                return false;
                            }
                            return result.Result.All(x => x);
                        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                shutdownStatus.ContinueWith(tr => Task.WhenAll(_transportMapping.Values.Select(x => x.Shutdown())).ContinueWith(
                          result =>
                          {
                              if (result.IsFaulted || result.IsCanceled)
                              {
                                  if (result.Exception != null)
                                      result.Exception.Handle(e => true);
                                  return false;
                              }
                              return result.Result.All(x => x) && tr.Result;
                          }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)).Unwrap().PipeTo(sender);


                foreach (var handoff in _pendingReadHandoffs.Values)
                {
                    handoff.Disassociate(DisassociateInfo.Shutdown);
                }

                //Ignore all other writes
                _normalShutdown = true;
                Become(Flushing);
            });
        }

        /// <summary>
        /// TBD
        /// </summary>
        protected void Flushing()
        {
            Receive<Send>(send => Context.System.DeadLetters.Tell(send));
            Receive<InboundAssociation>(
                     ia => ia.Association.AsInstanceOf<AkkaProtocolHandle>().Disassociate(DisassociateInfo.Shutdown));
            Receive<Terminated>(terminated => { }); // why should we care now?
        }

        #endregion

        #region Internal methods

        private void HandleInboundAssociation(InboundAssociation ia, bool writerIsIdle)
        {
            var readonlyEndpoint = _endpoints.ReadOnlyEndpointFor(ia.Association.RemoteAddress);
            var handle = ((AkkaProtocolHandle)ia.Association);
            if (readonlyEndpoint != null)
            {
                var endpoint = readonlyEndpoint.Item1;
                if (_pendingReadHandoffs.ContainsKey(endpoint)) _pendingReadHandoffs[endpoint].Disassociate();
                _pendingReadHandoffs.AddOrSet(endpoint, handle);
                endpoint.Tell(new EndpointWriter.TakeOver(handle, Self));
                _endpoints.WritableEndpointWithPolicyFor(handle.RemoteAddress).Match()
                    .With<Pass>(pass =>
                    {
                        pass.Endpoint.Tell(new ReliableDeliverySupervisor.Ungate());
                    });
            }
            else
            {
                if (_endpoints.IsQuarantined(handle.RemoteAddress, handle.HandshakeInfo.Uid))
                    handle.Disassociate(DisassociateInfo.Quarantined);
                else
                {
                    var policy = _endpoints.WritableEndpointWithPolicyFor(handle.RemoteAddress);
                    var pass = policy as Pass;
                    if (pass != null && !pass.Uid.HasValue)
                    {
                        // Idle writer will never send a GotUid or a Terminated so we need to "provoke it"
                        // to get an unstash event
                        if (!writerIsIdle)
                        {
                            pass.Endpoint.Tell(ReliableDeliverySupervisor.IsIdle.Instance);
                            var stashedInboundForEp = _stashedInbound.GetOrElse(pass.Endpoint,
                                new List<InboundAssociation>());
                            stashedInboundForEp.Add(ia);
                            _stashedInbound[pass.Endpoint] = stashedInboundForEp;
                        }
                        else
                        {
                            CreateAndRegisterEndpoint(handle, _endpoints.RefuseUid(handle.RemoteAddress));
                        }
                    }
                    else if (pass != null) // has a UID value
                    {
                        if (handle.HandshakeInfo.Uid == pass.Uid)
                        {
                            _pendingReadHandoffs.GetOrElse(pass.Endpoint, null)?.Disassociate();
                            _pendingReadHandoffs.AddOrSet(pass.Endpoint, handle);
                            pass.Endpoint.Tell(new EndpointWriter.StopReading(pass.Endpoint, Self));
                            pass.Endpoint.Tell(new ReliableDeliverySupervisor.Ungate());
                        }
                        else
                        {
                            Context.Stop(pass.Endpoint);
                            _endpoints.UnregisterEndpoint(pass.Endpoint);
                            _pendingReadHandoffs.Remove(pass.Endpoint);
                            CreateAndRegisterEndpoint(handle, pass.Uid);
                        }
                    }
                    else
                    {
                        CreateAndRegisterEndpoint(handle, _endpoints.RefuseUid(handle.RemoteAddress));
                    }
                }
            }
        }

        private Task<List<Tuple<ProtocolTransportAddressPair, TaskCompletionSource<IAssociationEventListener>>>>
            _listens;
        private Task<List<Tuple<ProtocolTransportAddressPair, TaskCompletionSource<IAssociationEventListener>>>>
            Listens
        {
            get
            {
                if (_listens == null)
                {
                    /*
                 * Constructs chains of adapters on top of each driven given in configuration. The result structure looks like the following:
                 * 
                 *      AkkaProtocolTransport <-- Adapter <-- ... <-- Adapter <-- Driver
                 * 
                 * The transports variable contains only the heads of each chains (the AkkaProtocolTransport instances)
                 */
                    var transports = new List<AkkaProtocolTransport>();
                    foreach (var transportSettings in _settings.Transports)
                    {
                        var args = new object[] { Context.System, transportSettings.Config };

                        //Loads the driver -- the bottom element of the chain
                        //The chain at this point:
                        //  Driver
                        Transport.Transport driver;
                        try
                        {
                            var driverType = Type.GetType(transportSettings.TransportClass);
                            if (driverType == null)
                                throw new TypeLoadException(
                                    $"Cannot instantiate transport [{transportSettings.TransportClass}]. Cannot find the type.");

                            if (!typeof(Transport.Transport).IsAssignableFrom(driverType))
                                throw new TypeLoadException(
                                    $"Cannot instantiate transport [{transportSettings.TransportClass}]. It does not implement [{typeof (Transport.Transport).FullName}].");

                            var constructorInfo = driverType.GetConstructor(new[] { typeof(ActorSystem), typeof(Config) });
                            if (constructorInfo == null)
                                throw new TypeLoadException(
                                    $"Cannot instantiate transport [{transportSettings.TransportClass}]. " +
                                    $"It has no public constructor with [{typeof (ActorSystem).FullName}] and [{typeof (Config).FullName}] parameters");

                            // ReSharper disable once AssignNullToNotNullAttribute
                            driver = (Transport.Transport)Activator.CreateInstance(driverType, args);
                        }
                        catch (Exception ex)
                        {
                            var ei = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                            var task = new Task<List<Tuple<ProtocolTransportAddressPair, TaskCompletionSource<IAssociationEventListener>>>>(() =>
                            {
                                ei.Throw();
                                return null;
                            });
                            task.RunSynchronously();
                            _listens = task;
                            return _listens;
                        }

                        //Iteratively decorates the bottom level driver with a list of adapters
                        //The chain at this point:
                        //  Adapter <-- .. <-- Adapter <-- Driver
                        var wrappedTransport = transportSettings.Adapters.Select(x => TransportAdaptersExtension.For(Context.System).GetAdapterProvider(x)).Aggregate(driver,
                            (transport, provider) => provider.Create(transport, (ExtendedActorSystem)Context.System));

                        //Apply AkkaProtocolTransport wrapper to the end of the chain
                        //The chain at this point:
                        // AkkaProtocolTransport <-- Adapter <-- .. <-- Adapter <-- Driver
                        transports.Add(new AkkaProtocolTransport(wrappedTransport, Context.System, new AkkaProtocolSettings(_conf), new AkkaPduProtobuffCodec()));
                    }

                    // Collect all transports, listen addresses, and listener promises in one Task
                    var tasks = transports.Select(x => x.Listen().ContinueWith(
                        result => Tuple.Create(new ProtocolTransportAddressPair(x, result.Result.Item1), result.Result.Item2), TaskContinuationOptions.ExecuteSynchronously));
                    _listens = Task.WhenAll(tasks).ContinueWith(transportResults => transportResults.Result.ToList(), TaskContinuationOptions.ExecuteSynchronously);
                }
                return _listens;
            }
        }

        private void AcceptPendingReader(IActorRef takingOverFrom)
        {
            if (_pendingReadHandoffs.ContainsKey(takingOverFrom))
            {
                var handle = _pendingReadHandoffs[takingOverFrom];
                _pendingReadHandoffs.Remove(takingOverFrom);
                _eventPublisher.NotifyListeners(new AssociatedEvent(handle.LocalAddress, handle.RemoteAddress, inbound: true));
                var endpoint = CreateEndpoint(handle.RemoteAddress, handle.LocalAddress,
                    _transportMapping[handle.LocalAddress], _settings, false, handle, refuseUid: null);
                _endpoints.RegisterReadOnlyEndpoint(handle.RemoteAddress, endpoint, handle.HandshakeInfo.Uid);
            }
        }

        private void RemovePendingReader(IActorRef takingOverFrom, AkkaProtocolHandle withHandle)
        {
            if (_pendingReadHandoffs.ContainsKey(takingOverFrom) &&
                _pendingReadHandoffs[takingOverFrom].Equals(withHandle))
            {
                _pendingReadHandoffs.Remove(takingOverFrom);
            }
        }

        private void CreateAndRegisterEndpoint(AkkaProtocolHandle handle, int? refuseUid)
        {
            var writing = _settings.UsePassiveConnections && !_endpoints.HasWriteableEndpointFor(handle.RemoteAddress);
            _eventPublisher.NotifyListeners(new AssociatedEvent(handle.LocalAddress, handle.RemoteAddress, true));
            var endpoint = CreateEndpoint(
                handle.RemoteAddress,
                handle.LocalAddress,
                _transportMapping[handle.LocalAddress],
                _settings,
                writing,
                handle,
                refuseUid);

            if (writing)
            {
                _endpoints.RegisterWritableEndpoint(handle.RemoteAddress, endpoint, handle.HandshakeInfo.Uid, refuseUid);
            }
            else
            {
                _endpoints.RegisterReadOnlyEndpoint(handle.RemoteAddress, endpoint, handle.HandshakeInfo.Uid);
                if (!_endpoints.HasWriteableEndpointFor(handle.RemoteAddress))
                    _endpoints.RemovePolicy(handle.RemoteAddress);
            }
        }

        private IActorRef CreateEndpoint(
            Address remoteAddress,
            Address localAddress,
            AkkaProtocolTransport transport,
            RemoteSettings endpointSettings,
            bool writing,
            AkkaProtocolHandle handleOption = null,
            int? refuseUid = null)
        {
            System.Diagnostics.Debug.Assert(_transportMapping.ContainsKey(localAddress));
            // refuseUid is ignored for read-only endpoints since the UID of the remote system is already known and has passed
            // quarantine checks

            IActorRef endpointActor;

            if (writing)
            {
                endpointActor =
                    Context.ActorOf(RARP.For(Context.System)
                    .ConfigureDispatcher(
                        ReliableDeliverySupervisor.ReliableDeliverySupervisorProps(handleOption, localAddress,
                            remoteAddress, refuseUid, transport, endpointSettings, new AkkaPduProtobuffCodec(),
                            _receiveBuffers, endpointSettings.Dispatcher)
                            .WithDeploy(Deploy.Local)),
                        string.Format("reliableEndpointWriter-{0}-{1}", AddressUrlEncoder.Encode(remoteAddress),
                            _endpointId.Next()));
            }
            else
            {
                endpointActor =
                    Context.ActorOf(RARP.For(Context.System)
                    .ConfigureDispatcher(
                        EndpointWriter.EndpointWriterProps(handleOption, localAddress, remoteAddress, refuseUid,
                            transport, endpointSettings, new AkkaPduProtobuffCodec(), _receiveBuffers,
                            reliableDeliverySupervisor: null)
                            .WithDeploy(Deploy.Local)),
                        string.Format("endpointWriter-{0}-{1}", AddressUrlEncoder.Encode(remoteAddress), _endpointId.Next()));
            }

            Context.Watch(endpointActor);
            return endpointActor;
        }

        #endregion

    }
}

