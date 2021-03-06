﻿namespace Eshopworld.Messaging
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Core;
    using JetBrains.Annotations;
    using Microsoft.Azure.Management.Fluent;
    using Microsoft.Azure.Management.ResourceManager.Fluent;
    using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
    using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
    using Microsoft.Azure.Management.ServiceBus.Fluent;
    using Microsoft.Azure.ServiceBus;
    using Microsoft.Azure.ServiceBus.Core;
    using Microsoft.Azure.Services.AppAuthentication;
    using Microsoft.Rest;
    using Newtonsoft.Json;

    internal abstract class ServiceBusAdapter<T> : ServiceBusAdapter
        where T : class
    {
        internal readonly IDictionary<T, Message> Messages = new Dictionary<T, Message>(ObjectReferenceEqualityComparer<T>.Default);
        internal readonly IDictionary<T, Timer> LockTimers = new Dictionary<T, Timer>(ObjectReferenceEqualityComparer<T>.Default);
        internal readonly IObserver<T> MessagesIn;
        internal readonly bool RawMessages;

        internal MessageReceiver Receiver;
        internal Timer ReadTimer;
        internal int BatchSize;

        internal readonly string ConnectionString;

        internal long LockInSeconds;
        internal long LockTickInSeconds;

        protected ServiceBusAdapter([NotNull] string connectionString, [NotNull] string subscriptionId, [NotNull]IObserver<T> messagesIn, int batchSize, Type typeOverride)
            : base(connectionString, subscriptionId, typeOverride ?? typeof(T))
        {
            MessagesIn = messagesIn;
            BatchSize = batchSize;
            ConnectionString = connectionString;

            RawMessages = typeof(T) == typeof(Message);
        }

        /// <summary>
        /// Releases a message from the Queue by releasing all the specific message resources like lock
        /// renewal timers.
        /// This is called by all the methods that terminate the life of a message like COMPLETE, ABANDON and ERROR.
        /// </summary>
        /// <param name="message">The message that we want to release.</param>
        internal void Release([NotNull]T message)
        {
            lock (Gate)
            {
                if (!RawMessages)
                {
                    Messages.Remove(message);
                }

                // check for a lock renewal timer and release it if it exists
                if (LockTimers.ContainsKey(message))
                {
                    LockTimers[message]?.Dispose();
                    LockTimers.Remove(message);
                }
            }
        }

        /// <summary>
        /// Completes a message by doing the actual READ from the queue.
        /// </summary>
        /// <param name="message">The message we want to complete.</param>
        internal async Task Complete(T message)
        {
            var m = RawMessages ? message as Message : Messages[message];

            await Receiver.CompleteAsync(m?.SystemProperties.LockToken).ConfigureAwait(false);
            Release(message);
        }

        /// <summary>
        /// Abandons a message by returning it to the queue.
        /// </summary>
        /// <param name="message">The message we want to abandon.</param>
        internal async Task Abandon(T message)
        {
            var m = RawMessages ? message as Message : Messages[message];

            await Receiver.AbandonAsync(m?.SystemProperties.LockToken).ConfigureAwait(false);
            Release(message);
        }

        /// <summary>
        /// Errors a message by moving it specifically to the error queue.
        /// </summary>
        /// <param name="message">The message that we want to move to the error queue.</param>
        internal async Task Error(T message)
        {
            var m = RawMessages ? message as Message : Messages[message];

            await Receiver.DeadLetterAsync(m?.SystemProperties.LockToken).ConfigureAwait(false);
            Release(message);
        }

        /// <summary>
        /// Creates a perpetual lock on a message by continuously renewing it's lock.
        /// This is usually created at the start of a handler so that we guarantee that we still have a valid lock
        /// and we retain that lock until we finish handling the message.
        /// </summary>
        /// <param name="message">The message that we want to create the lock on.</param>
        internal async Task Lock(T message)
        {
            var m = RawMessages ? message as Message : Messages[message];

            await Receiver.RenewLockAsync(m).ConfigureAwait(false);

            LockTimers.Add(
                message,
                new Timer(
                    async _ => { await Receiver.RenewLockAsync(Messages[message]).ConfigureAwait(false); },
                    null,
                    TimeSpan.FromSeconds(LockTickInSeconds),
                    TimeSpan.FromSeconds(LockTickInSeconds)));
        }

        /// <summary>
        /// [BATCHED] Read message call back.
        /// </summary>
        /// <param name="_">[Ignored]</param>
        internal async Task Read([CanBeNull]object _)
        {
            if (Receiver.IsClosedOrClosing) return;

            var messages = await Receiver.ReceiveAsync(BatchSize).ConfigureAwait(false);
            if (messages == null) return;

            foreach (var message in messages)
            {
                if (RawMessages)
                {
                    MessagesIn.OnNext(message as T);
                }
                else
                {
                    var messageBody = JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(message.Body));
                    Messages[messageBody] = message;
                    MessagesIn.OnNext(messageBody);
                }
            }
        }

        /// <summary>
        /// Stops pooling the queue for reading messages.
        /// </summary>
        internal void StopReading()
        {
            ReadTimer.Dispose();
            ReadTimer = null;
        }

        /// <summary>
        /// Sets the size of the message batch during receives.
        /// </summary>
        /// <param name="batchSize">The size of the batch when reading for a queue - used as the pre-fetch parameter of the </param>
        internal void SetBatchSize(int batchSize)
        {
            BatchSize = batchSize;
            Receiver.PrefetchCount = batchSize;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ReadTimer?.Dispose();
                Receiver?.CloseAsync().Wait();
            }
        }
    }

    /// <summary>
    /// Non generic message queue/topic router from <see cref="IObservable{IMessage}"/> through to the ServiceBus entities.
    /// </summary>
    internal abstract class ServiceBusAdapter : IDisposable
    {
        [SuppressMessage("Critical Code Smell", "S2223:Non-constant static fields should not be visible", Justification = "Performance")]
        protected static IServiceBusNamespace AzureServiceBusNamespace;
        internal readonly object Gate = new object();


        /// <summary>
        /// Initializes a new instance of <see cref="ServiceBusAdapter"/>.
        /// </summary>
        /// <param name="connectionString">The Azure Service Bus connection string.</param>
        /// <param name="subscriptionId">The ID of the subscription where the service bus namespace lives.</param>
        /// <param name="messageType">The fully strongly typed <see cref="Type"/> of the message we want to create the queue for.</param>
        [SuppressMessage("Major Code Smell", "S3010:Static fields should not be updated in constructors", Justification = "Performance")]
        protected ServiceBusAdapter([NotNull]string connectionString, [NotNull]string subscriptionId, [NotNull]Type messageType)
        {
            if (messageType.FullName?.Length > 260) // SB quota: Entity path max length
            {
                throw new InvalidOperationException(
                    $@"You can't create queues for the type {messageType.FullName} because the full name (namespace + name) exceeds 260 characters.
I suggest you reduce the size of the namespace: '{messageType.Namespace}'.");
            }

            var namespaceName = connectionString.GetNamespaceNameFromConnectionString();

            if (AzureServiceBusNamespace == null)
            {
                var token = new AzureServiceTokenProvider().GetAccessTokenAsync("https://management.core.windows.net/", string.Empty).Result;
                var tokenCredentials = new TokenCredentials(token);

                var client = RestClient.Configure()
                    .WithEnvironment(AzureEnvironment.AzureGlobalCloud)
                    .WithLogLevel(HttpLoggingDelegatingHandler.Level.Basic)
                    .WithCredentials(new AzureCredentials(tokenCredentials, tokenCredentials, string.Empty, AzureEnvironment.AzureGlobalCloud))
                    .Build();

                AzureServiceBusNamespace = Azure.Authenticate(client, string.Empty)
                    .WithSubscription(subscriptionId)
                    .ServiceBusNamespaces.List()
                    .SingleOrDefault(n => n.Name == namespaceName);

                if (AzureServiceBusNamespace == null)
                {
                    throw new InvalidOperationException($"Couldn't find the service bus namespace {namespaceName} in the subscription with ID {subscriptionId}");
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected abstract void Dispose(bool disposing);
    }

    internal enum MessagingTransport
    {
        Queue,
        Topic
    }
}