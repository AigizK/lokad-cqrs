﻿using System;
using System.Collections.Generic;
using System.Concurrency;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Lokad.Cqrs.Build.Engine;
using Lokad.Cqrs.Core.Dispatch.Events;
using NUnit.Framework;

namespace Lokad.Cqrs
{
    public abstract class FiniteEngineScenario
    {
        protected bool HandlerFailuresAreExpected { get; set; }
        protected readonly IList<object> StartupMessages = new List<object>();

        protected virtual void Configure(CloudEngineBuilder builder) {}
        readonly List<string> _failures = new List<string>();
        readonly List<Func<IObservable<ISystemEvent>, IMessageSender, CancellationTokenSource, IDisposable>> _subscriptions = new List<Func<IObservable<ISystemEvent>, IMessageSender, CancellationTokenSource, IDisposable>>();

        protected void Enlist(Func<IObservable<ISystemEvent>, IMessageSender, CancellationTokenSource, IDisposable> subscribe)
        {
            _subscriptions.Add(subscribe);
        }

        protected FiniteEngineScenario()
        {
            Enlist((events, sender, t) => events
                .OfType<EnvelopeAcked>()
                .Where(ea => ea.Attributes.Any(p => p.Key == "finish")).Subscribe(c => t.Cancel()));

            Enlist((events, sender, t) => events
                .OfType<EnvelopeAcked>()
                .Where(ea => ea.Attributes.Any(p => p.Key == "fail"))
                .Subscribe(c =>
                    {
                        _failures.Add((string) c.Attributes.First(p => p.Key == "fail").Value);
                        t.Cancel();
                    }));

            Enlist((events, sender, t) => events
                .OfType<EnvelopeDispatchFailed>()
                .Subscribe(d =>
                {
                    if (HandlerFailuresAreExpected) return;
                    _failures.Add(d.Exception.ToString());
                    t.Cancel();
                }));
        }


        public void TestConfiguration(Action<CloudEngineBuilder> config)
        {
            var scenario = this;
            var events = new Subject<ISystemEvent>(Scheduler.TaskPool);
            var builder = new CloudEngineBuilder()
                .EnlistObserver(events);

            Configure(builder);
            config(builder);

            var disposables = new List<IDisposable>();
            
            try
            {
                using (var engine = builder.Build())
                using (var t = new CancellationTokenSource())
                {
                    var sender = engine.Resolve<IMessageSender>();
                    foreach (var subscription in _subscriptions)
                    {
                        disposables.Add(subscription(events,sender, t));
                    }
                    


                    engine.Start(t.Token);

                    foreach (var message in scenario.StartupMessages)
                    {
                        sender.SendOne(message);
                    }

                    if (Debugger.IsAttached)
                    {
                        t.Token.WaitHandle.WaitOne();
                    }
                    else
                    {
                        t.Token.WaitHandle.WaitOne(5000);
                    }

                    

                    if (_failures.Any())
                    {
                        Assert.Fail(_failures.First());
                    }

                    if (!t.IsCancellationRequested)
                    {
                        Assert.Fail("Engine should be stopped manually!");
                    }

                }
            }
            finally
            {
                foreach (var disposable in disposables)
                {
                    disposable.Dispose();
                }
            }
        }
    }
}