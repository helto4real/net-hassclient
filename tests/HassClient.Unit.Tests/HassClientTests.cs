using JoySoftware.HomeAssistant.Client;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace HassClient.Unit.Tests
{
    public class HassClientTests
    {
        [Theory]
        [InlineData(EventType.All)]
        [InlineData(EventType.ServiceRegistered)]
        [InlineData(EventType.CallService)]
        [InlineData(EventType.ComponentLoaded)]
        [InlineData(EventType.HomeAssistantStart)]
        [InlineData(EventType.PlatformDiscovered)]
        [InlineData(EventType.ServiceExecuted)]
        [InlineData(EventType.StateChanged)]
        [InlineData(EventType.TimeChanged)]
        [InlineData(EventType.HomeAssistantStop)]
        public async void SubscriptToEventTypeShouldReturnEvent(EventType eventType)
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the non connected hass client
            var hassClient = await mock.GetHassConnectedClient();

            // ACT AND ASSERT
            var subscribeTask = hassClient.SubscribeToEvents(eventType);
            mock.AddResponse(@"{""id"": 2, ""type"": ""result"", ""success"": true, ""result"": null}");
            Assert.True(await subscribeTask);
            mock.AddResponse(HassWebSocketMock.EventMessage);
            HassEvent eventMsg = await hassClient.ReadEventAsync();
            Assert.NotNull(eventMsg);
        }

        [Fact]
        public async void CallServiceIfCanceledShouldThrowOperationCanceledException()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the non connected hass client
            var hassClient = await mock.GetHassConnectedClient();

            // Do not add a fake service call message result 

            // ACT
            var callServiceTask = hassClient.CallService("light", "turn_on", new { entity_id = "light.tomas_rum" });
            hassClient.CancelSource.Cancel();

            // ASSERT
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await callServiceTask);
        }

        [Fact]
        public async void CallServiceSuccessfulReturnsTrue()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the non connected hass client
            var hassClient = await mock.GetHassConnectedClient();

            // Service call successful
            mock.AddResponse(@"{
                                      ""id"": 2,
                                      ""type"": ""result"",
                                      ""success"": true,
                                      ""result"": {
                                        ""context"": {
                                          ""id"": ""55cf75a4dbf94680804ef022aa0c67b4"",
                                          ""parent_id"": null,
                                          ""user_id"": ""63b2952cb986474d84be46480c8aaad3""
                                        }
                                      }
                                    }");

            // ACT
            var result = await hassClient.CallService("light", "turn_on", new { entity_id = "light.tomas_rum" });

            // Assert 
            Assert.True(result);
        }

        [Fact]
        public async void CallServiceUnhandledErrorThrowsException()
        {
            // ARRANGE
            var webSocketMock = Mock.Of<IClientWebSocket>(ws =>
                ws.SendAsync(null, WebSocketMessageType.Text, true, CancellationToken.None) ==
                Task.FromException(new Exception("Some exception")));

            var factoryMock = Mock.Of<IClientWebSocketFactory>(mf =>
                mf.New() == webSocketMock
            );

            var hc = new JoySoftware.HomeAssistant.Client.HassClient(wsFactory: factoryMock);

            // ACT AND ASSERTs
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await hc.SubscribeToEvents());
        }

        [Fact]
        public async void CallServiceWithTimeoutShouldReturnFalse()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the non connected hass client
            var hassClient = await mock.GetHassConnectedClient();
            hassClient.SocketTimeout = 10;

            // ACT AND ASSERT

            // Do not add a message and force timeout
            Assert.False(await hassClient.CallService("light", "turn_on", new { entity_id = "light.tomas_rum" }));
        }

        [Fact]
        public async void ClientGetUnexpectedMessageRecoversResultNotNull()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the non connected hass client
            var hassClient = await mock.GetHassConnectedClient();

            // ACT
            var confTask = hassClient.GetConfig();

            // First add an unexpected message, message id should be 2
            mock.AddResponse(@"{""id"": 12345, ""type"": ""result"", ""success"": false, ""result"": null}");
            // Then add the expected one... It should recover from that...
            mock.AddResponse(HassWebSocketMock.ConfigMessage);

            // ASSERT
            Assert.NotNull(await confTask);
        }

        [Fact]
        public async Task CloseAsyncIsRanOnce()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the non connected hass client
            var hassClient = await mock.GetHassConnectedClient();

            await hassClient.CloseAsync();

            // ASSERT
            mock.Verify(
                x => x.CloseAsync(It.IsAny<WebSocketCloseStatus>(),
                    It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        //TODO: Fix the test
        [Fact]
        public async void CloseAsyncWithTimeoutThrowsOperationCanceledExceotion()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the non connected hass client
            var hassClient = await mock.GetHassConnectedClient();

            mock.Setup(x =>
                    x.CloseAsync(It.IsAny<WebSocketCloseStatus>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.FromException<OperationCanceledException>(new OperationCanceledException("Fake close")));

            hassClient.SocketTimeout = 20;

            // ACT
            await hassClient.CloseAsync();

            // ASSERT
            mock.Logger.AssertLogged(LogLevel.Trace, Times.AtLeastOnce());
        }

        [Fact]
        public async void CommandWithUnsuccessfulShouldThrowAggregateException()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the non connected hass client
            var hassClient = await mock.GetHassConnectedClient();

            // ACT
            Task<HassConfig> confTask = hassClient.GetConfig();

            // Add result not success message
            mock.AddResponse(@"{""id"": 2, ""type"": ""result"", ""success"": false, ""result"": null}");

            // ASSERT
            Assert.Throws<AggregateException>(() => confTask.Result);
        }

        [Fact]
        public async void ConfigShouldBeCorrect()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the non connected hass client
            var hassClient = await mock.GetHassConnectedClient();

            // ACT
            Task<HassConfig> getConfigTask = hassClient.GetConfig();
            // Fake return Config message, check result_config.json for reference
            mock.AddResponse(HassWebSocketMock.ConfigMessage);

            var conf = getConfigTask.Result;

            // ASSERT, its an object assertion here so multiple asserts allowed
            // Check result_config.json for reference
            Assert.NotNull(conf);
            Assert.Equal("°C", conf.UnitSystem?.Temperature);
            Assert.Equal("km", conf.UnitSystem?.Length);
            Assert.Equal("g", conf.UnitSystem?.Mass);
            Assert.Equal("L", conf.UnitSystem?.Volume);

            Assert.Contains("binary_sensor.deconz", conf.Components);
            Assert.Equal(62.2398549F, conf.Latitude);
            Assert.Equal(15.4412267F, conf.Longitude);
            Assert.Equal(49, conf.Elevation);

            Assert.Contains("/config/www", conf.WhitelistExternalDirs);
            Assert.Equal("0.87.0", conf.Version);
            Assert.Equal("Home", conf.LocationName);

            Assert.Equal("/config", conf.ConfigDir);
            Assert.Equal("Europe/Stockholm", conf.TimeZone);
        }

        [Fact]
        public async void ConnectAlreadyConnectedThrowsInvalidOperation()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the non connected hass client
            var hassClient = await mock.GetHassConnectedClient();

            // ACT AND ASSERT

            // The hass client already connected and should assert error
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await hassClient.ConnectAsync(new Uri("ws://localhost:8192/api/websocket"), "TOKEN", false));
        }

        [Fact]
        public async void ConnectShouldReturnTrue()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the default state hass client
            var hassClient = mock.GetHassClient();

            // First message from Home Assistant is auth required
            mock.AddResponse(@"{""type"": ""auth_required""}");
            // Next one we fake it is auth ok
            mock.AddResponse(@"{""type"": ""auth_ok""}");

            // ACT and ASSERT
            // Calls connect without getting the states initially
            Assert.True(await hassClient.ConnectAsync(new Uri("ws://anyurldoesntmatter.org"), "FAKETOKEN", false));
        }

        [Fact]
        public async void ConnectTimeoutReturnsFalse()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the default state hass client and we add no response messages
            var hassClient = mock.GetHassClient();

            // Set the timeout to a very low value for testing purposes
            hassClient.SocketTimeout = 20;

            // ACT AND ASSERT
            Assert.False(await hassClient.ConnectAsync(new Uri("ws://localhost:8192/api/websocket"), "TOKEN", false));
        }

        [Fact]
        public async void ConnectWithAuthFailLogsErrorAndReturnFalse()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the default state hass client
            var hassClient = mock.GetHassClient();

            // First message from Home Assistant is auth required
            mock.AddResponse(@"{""type"": ""auth_required""}");
            // Next one we fake it is auth ok
            mock.AddResponse(@"{""type"": ""auth_invalid""}");

            // ACT and ASSERT
            // Calls connect without getting the states initially
            Assert.False(await hassClient.ConnectAsync(new Uri("ws://anyurldoesntmatter.org"), "FAKETOKEN", false));
            // Make sure we logged the error.
            mock.Logger.AssertLogged(LogLevel.Error, Times.AtLeastOnce());
        }

        [Fact]
        public async void ConnectWithUriNullThrowsArgumentNullException()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the default state hass client and we add no response messages
            var hassClient = mock.GetHassClient();

            await Assert.ThrowsAsync<ArgumentNullException>(
                async () => await hassClient.ConnectAsync(null, "lss", false));
        }

        [Fact]
        public async void NoPongMessagePingShouldReturnFalse()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the default state hass client
            var hassClient = await mock.GetHassConnectedClient();

            // No pong message is sent from server...

            // ACT and ASSERT
            Assert.False(await hassClient.PingAsync(2));
        }

        [Fact]
        public async void PingShouldReturnTrue()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the default state hass client
            var hassClient = await mock.GetHassConnectedClient();

            // Fake return pong message
            mock.AddResponse(@"{""type"": ""pong""}");

            // ACT and ASSERT
            Assert.True(await hassClient.PingAsync(1000));
        }

        [Fact]
        public async Task ReturningStatesTheCountShouldBeNineteen()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the non connected hass client
            var hassClient = mock.GetHassClientNotConnected();

            hassClient.SocketTimeout = 50000;
            // ACT

            var connectTask = hassClient.ConnectAsync(new Uri("ws://localhost:8192/api/websocket"), "TOKEN");

            // Wait until hassclient processes connect sequence
            await mock.WaitUntilConnected();

            // Fake return states message
            mock.AddResponse(HassWebSocketMock.StateMessage);
            await connectTask;

            // ASSERT
            Assert.Equal(19, hassClient.States.Count);
        }

        [Fact]
        public async void ServiceEventShouldHaveCorrectObject()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the non connected hass client
            var hassClient = await mock.GetHassConnectedClient();

            // Add the service message fake , check service_event.json for reference
            mock.AddResponse(HassWebSocketMock.ServiceMessage);

            // ACT
            var result = await hassClient.ReadEventAsync();
            var serviceEvent = result?.Data as HassServiceEventData;
            JsonElement? c = serviceEvent?.ServiceData?.GetProperty("entity_id");

            // ASSERT
            Assert.NotNull(serviceEvent);
            Assert.Equal("light", serviceEvent.Domain);
            Assert.Equal("toggle", serviceEvent.Service!);
            Assert.Equal("light.tomas_rum", c?.GetString());
        }

        [Fact]
        public async void SubscribeToEventsReturnsCorrectEvent()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the non connected hass client
            var hassClient = await mock.GetHassConnectedClient();

            var subscribeTask = hassClient.SubscribeToEvents();
            // Add result success
            mock.AddResponse(@"{""id"": 2, ""type"": ""result"", ""success"": true, ""result"": null}");
            await subscribeTask;

            // Add response event message, see event.json as reference
            mock.AddResponse(HassWebSocketMock.EventMessage);

            // ACT
            HassEvent eventMsg = await hassClient.ReadEventAsync();

            // ASSERT, object multiple assertions
            Assert.NotNull(eventMsg);

            Assert.Equal("LOCAL", eventMsg.Origin);
            Assert.Equal(DateTime.Parse("2019-02-17T11:43:47.090511+00:00"), eventMsg.TimeFired);

            var stateMessage = eventMsg.Data as HassStateChangedEventData;

            Assert.True(stateMessage?.EntityId == "binary_sensor.vardagsrum_pir");

            Assert.True(stateMessage.OldState?.EntityId == "binary_sensor.vardagsrum_pir");
            Assert.True(stateMessage.OldState?.Attributes != null &&
                        ((JsonElement)stateMessage.OldState?.Attributes?["battery_level"]).GetInt32()! == 100);
            Assert.True(((JsonElement)stateMessage.OldState?.Attributes?["on"]).GetBoolean()!);
            Assert.True(((JsonElement)stateMessage.OldState?.Attributes?["friendly_name"]).GetString()! ==
                        "Rörelsedetektor TV-rum");

            // Test the date and time conversions that it matches UTC time
            DateTime? lastChanged = stateMessage.OldState?.LastChanged;
            // Convert utc date to local so we can compare, this test will be ok on any timezone
            DateTime target = new DateTime(2019, 2, 17, 11, 41, 08, DateTimeKind.Utc).ToLocalTime();

            Assert.True(lastChanged.Value.Year == target.Year);
            Assert.True(lastChanged.Value.Month == target.Month);
            Assert.True(lastChanged.Value.Day == target.Day);
            Assert.True(lastChanged.Value.Hour == target.Hour);
            Assert.True(lastChanged.Value.Minute == target.Minute);
            Assert.True(lastChanged.Value.Second == target.Second);
        }

        [Fact]
        public async void SubscribeToEventsReturnsTrue()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the non connected hass client
            var hassClient = await mock.GetHassConnectedClient();

            // ACT
            var subscribeTask = hassClient.SubscribeToEvents();
            // Add result success
            mock.AddResponse(@"{""id"": 2, ""type"": ""result"", ""success"": true, ""result"": null}");

            // ASSERT
            Assert.True(await subscribeTask);
        }

        [Fact]
        public async void WrongMessagesFromHassShouldReturnFalse()
        {
            // ARRANGE
            var mock = new HassWebSocketMock();
            // Get the default state hass client
            var hassClient = mock.GetHassClient();

            // First message from Home Assistant is auth required
            mock.AddResponse(@"{""type"": ""auth_required""}");
            // Next one we fake it is auth ok
            mock.AddResponse(@"{""type"": ""result""}");

            // ACT and ASSERT
            // Calls connect without getting the states initially
            Assert.False(await hassClient.ConnectAsync(new Uri("ws://anyurldoesntmatter.org"), "FAKETOKEN", false));
        }
    }
}