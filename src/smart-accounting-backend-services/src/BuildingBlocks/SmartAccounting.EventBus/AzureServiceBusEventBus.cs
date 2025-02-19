﻿using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using SmartAccounting.EventBus.Configuration;
using SmartAccounting.EventBus.Interfaces;
using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SmartAccounting.EventBus
{
    public class AzureServiceBusEventBus : IEventBus
    {
        private readonly IEventBusConfiguration _eventBusConfiguration;
        private readonly IServiceProvider _serviceProvider;
        private readonly IEventBusSubscriptionsManager _subscriptionManager;
        private readonly ILogger<AzureServiceBusEventBus> _logger;
        private readonly ServiceBusClient _serviceBusClient;
        private readonly ServiceBusProcessor _serviceBusReceiver;
        private readonly ServiceBusAdministrationClient _serviceBusAdministrationClient;

        public AzureServiceBusEventBus(IEventBusConfiguration eventBusConfiguration,
                                       IServiceProvider serviceProvider,
                                       IEventBusSubscriptionsManager subscriptionManager,
                                       ILogger<AzureServiceBusEventBus> logger,
                                       ServiceBusClient serviceBusClient,
                                       ServiceBusProcessor serviceBusReceiver,
                                       ServiceBusAdministrationClient serviceBusAdministrationClient)
        {
            _eventBusConfiguration = eventBusConfiguration ?? throw new ArgumentNullException(nameof(eventBusConfiguration));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _subscriptionManager = subscriptionManager ?? throw new ArgumentNullException(nameof(subscriptionManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceBusClient = serviceBusClient ?? throw new ArgumentNullException(nameof(serviceBusClient));
            _serviceBusReceiver = serviceBusReceiver ?? throw new ArgumentNullException(nameof(serviceBusReceiver));
            _serviceBusAdministrationClient = serviceBusAdministrationClient
                                              ?? throw new ArgumentNullException(nameof(serviceBusAdministrationClient));
        }

        public async Task SetupAsync()
        {
            await RemoveDefaultRuleAsync();
            await RegisterSubscriptionClientMessageHandlerAsync();
        }

        public async Task PublishAsync(IntegrationEvent @event)
        {
            var eventName = @event.GetType().Name;
            var jsonMessage = JsonSerializer.Serialize(@event, @event.GetType());
            var body = Encoding.UTF8.GetBytes(jsonMessage);

            var message = new ServiceBusMessage
            {
                MessageId = Guid.NewGuid().ToString(),
                Subject = eventName,
                Body = new BinaryData(body)
            };

            var topicClient = _serviceBusClient.CreateSender(_eventBusConfiguration.TopicName);
            await topicClient.SendMessageAsync(message);
        }

        public async Task SubscribeAsync<T, TH>()
            where T : IntegrationEvent
            where TH : IIntegrationEventHandler<T>
        {
            var eventName = typeof(T).Name;

            var containsKey = _subscriptionManager.HasSubscriptionsForEvent<T>();
            if (!containsKey)
            {
                try
                {
                    await _serviceBusAdministrationClient.CreateRuleAsync(_eventBusConfiguration.TopicName,
                                                                          _eventBusConfiguration.Subscription,
                                                                          new CreateRuleOptions
                                                                          {
                                                                              Filter = new CorrelationRuleFilter
                                                                              {
                                                                                  Subject = eventName
                                                                              },
                                                                              Name = eventName
                                                                          });
                }
                catch (ServiceBusException)
                {
                    _logger.LogWarning("The messaging entity '{eventName}' already exists.", eventName);
                }
            }

            _logger.LogInformation("Subscribing to event '{EventName}' with '{EventHandler}'", eventName, typeof(TH).Name);
            _subscriptionManager.AddSubscription<T, TH>();
        }

        public async Task UnsubscribeAsync<T, TH>()
            where T : IntegrationEvent
            where TH : IIntegrationEventHandler<T>
        {
            var eventName = typeof(T).Name;

            await _serviceBusAdministrationClient.DeleteRuleAsync(_eventBusConfiguration.TopicName,
                                                                  _eventBusConfiguration.Subscription,
                                                                  eventName);

            _logger.LogInformation("Unsubscribing from event '{EventName}'", eventName);
            _subscriptionManager.RemoveSubscription<T, TH>();
        }

        private async Task RegisterSubscriptionClientMessageHandlerAsync()
        {
            _serviceBusReceiver.ProcessMessageAsync += MessageHandler;

            _serviceBusReceiver.ProcessErrorAsync += ErrorHandler;

            await _serviceBusReceiver.StartProcessingAsync();
        }

        private Task ErrorHandler(ProcessErrorEventArgs arg)
        {
            _logger.LogError($"Service Bus Message processing failed: {arg.ErrorSource} {arg.Exception.Message}");
            return Task.CompletedTask;
        }

        private async Task MessageHandler(ProcessMessageEventArgs arg)
        {
            var eventName = arg.Message.Subject;
            if (_subscriptionManager.HasSubscriptionsForEvent(eventName))
            {
                var subscriptions = _subscriptionManager.GetHandlersForEvent(eventName);
                foreach (var subscription in subscriptions)
                {
                    var handler = _serviceProvider.GetService(subscription.HandlerType);
                    if (handler == null) continue;

                    var eventType = _subscriptionManager.GetEventTypeByName(eventName);
                    var messageData = Encoding.UTF8.GetString(arg.Message.Body);

                    var integrationEvent = JsonSerializer.Deserialize(messageData, eventType);
                    var concreteType = typeof(IIntegrationEventHandler<>).MakeGenericType(eventType);
                    await (Task)concreteType.GetMethod("HandleAsync").Invoke(handler, new object[] { integrationEvent });
                    await arg.CompleteMessageAsync(arg.Message);
                }
            }
        }

        private async Task RemoveDefaultRuleAsync()
        {
            try
            {
                await _serviceBusAdministrationClient.DeleteRuleAsync(_eventBusConfiguration.TopicName,
                                                                      _eventBusConfiguration.Subscription,
                                                                      RuleProperties.DefaultRuleName);
            }
            catch (ServiceBusException ex)
            {
                var reason = ex.Reason.ToString();
                var errorMessage = ex.Message;
                _logger.LogError($"Service Bus request to remove default subscription failed: {reason} {errorMessage}");
            }

            catch (UnauthorizedAccessException ex)
            {
                var errorMessage = ex.Message;
                _logger.LogError($"Service Bus request to remove default subscription failed : {errorMessage}");
            }
        }
    }
}
