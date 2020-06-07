﻿/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/openiddict/openiddict-core for more information concerning
 * the license and the contributors participating to this project.
 */

using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OpenIddict.Abstractions;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace OpenIddict.Server.FunctionalTests
{
    public abstract partial class OpenIddictServerIntegrationTests
    {
        [Theory]
        [InlineData(nameof(HttpMethod.Delete))]
        [InlineData(nameof(HttpMethod.Head))]
        [InlineData(nameof(HttpMethod.Options))]
        [InlineData(nameof(HttpMethod.Put))]
        [InlineData(nameof(HttpMethod.Trace))]
        public async Task ExtractLogoutRequest_UnexpectedMethodReturnsAnError(string method)
        {
            // Arrange
            await using var server = await CreateServerAsync(options => options.EnableDegradedMode());
            await using var client = await server.CreateClientAsync();

            // Act
            var response = await client.SendAsync(method, "/connect/logout", new OpenIddictRequest());

            // Assert
            Assert.Equal(Errors.InvalidRequest, response.Error);
            Assert.Equal("The specified HTTP method is not valid.", response.ErrorDescription);
        }

        [Theory]
        [InlineData("custom_error", null, null)]
        [InlineData("custom_error", "custom_description", null)]
        [InlineData("custom_error", "custom_description", "custom_uri")]
        [InlineData(null, "custom_description", null)]
        [InlineData(null, "custom_description", "custom_uri")]
        [InlineData(null, null, "custom_uri")]
        [InlineData(null, null, null)]
        public async Task ExtractLogoutRequest_AllowsRejectingRequest(string error, string description, string uri)
        {
            // Arrange
            await using var server = await CreateServerAsync(options =>
            {
                options.EnableDegradedMode();

                options.AddEventHandler<ExtractLogoutRequestContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        context.Reject(error, description, uri);

                        return default;
                    }));
            });

            await using var client = await server.CreateClientAsync();

            // Act
            var response = await client.PostAsync("/connect/logout", new OpenIddictRequest());

            // Assert
            Assert.Equal(error ?? Errors.InvalidRequest, response.Error);
            Assert.Equal(description, response.ErrorDescription);
            Assert.Equal(uri, response.ErrorUri);
        }

        [Fact]
        public async Task ExtractLogoutRequest_AllowsHandlingResponse()
        {
            // Arrange
            await using var server = await CreateServerAsync(options =>
            {
                options.EnableDegradedMode();

                options.AddEventHandler<ExtractLogoutRequestContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        context.Transaction.SetProperty("custom_response", new
                        {
                            name = "Bob le Bricoleur"
                        });

                        context.HandleRequest();

                        return default;
                    }));
            });

            await using var client = await server.CreateClientAsync();

            // Act
            var response = await client.GetAsync("/connect/logout");

            // Assert
            Assert.Equal("Bob le Bricoleur", (string) response["name"]);
        }

        [Fact]
        public async Task ExtractLogoutRequest_AllowsSkippingHandler()
        {
            // Arrange
            await using var server = await CreateServerAsync(options =>
            {
                options.EnableDegradedMode();

                options.AddEventHandler<ExtractLogoutRequestContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        context.SkipRequest();

                        return default;
                    }));
            });

            await using var client = await server.CreateClientAsync();

            // Act
            var response = await client.GetAsync("/connect/logout");

            // Assert
            Assert.Equal("Bob le Magnifique", (string) response["name"]);
        }

        [Theory]
        [InlineData("/path", "The 'post_logout_redirect_uri' parameter must be a valid absolute URL.")]
        [InlineData("/tmp/file.xml", "The 'post_logout_redirect_uri' parameter must be a valid absolute URL.")]
        [InlineData("C:\\tmp\\file.xml", "The 'post_logout_redirect_uri' parameter must be a valid absolute URL.")]
        [InlineData("http://www.fabrikam.com/path#param=value", "The 'post_logout_redirect_uri' parameter must not include a fragment.")]
        public async Task ValidateLogoutRequest_RequestIsRejectedWhenRedirectUriIsInvalid(string address, string message)
        {
            // Arrange
            await using var server = await CreateServerAsync();
            await using var client = await server.CreateClientAsync();

            // Act
            var response = await client.PostAsync("/connect/logout", new OpenIddictRequest
            {
                PostLogoutRedirectUri = address
            });

            // Assert
            Assert.Equal(Errors.InvalidRequest, response.Error);
            Assert.Equal(message, response.ErrorDescription);
        }

        [Fact]
        public async Task ValidateLogoutRequest_RequestIsRejectedWhenNoMatchingApplicationIsFound()
        {
            // Arrange
            var manager = CreateApplicationManager(mock =>
            {
                mock.Setup(manager => manager.FindByPostLogoutRedirectUriAsync("http://www.fabrikam.com/path", It.IsAny<CancellationToken>()))
                    .Returns(AsyncEnumerable.Empty<OpenIddictApplication>());
            });

            await using var server = await CreateServerAsync(options =>
            {
                options.Services.AddSingleton(manager);
            });

            await using var client = await server.CreateClientAsync();

            // Act
            var response = await client.PostAsync("/connect/logout", new OpenIddictRequest
            {
                PostLogoutRedirectUri = "http://www.fabrikam.com/path"
            });

            // Assert
            Assert.Equal(Errors.InvalidRequest, response.Error);
            Assert.Equal("The specified 'post_logout_redirect_uri' parameter is not valid.", response.ErrorDescription);

            Mock.Get(manager).Verify(manager => manager.FindByPostLogoutRedirectUriAsync("http://www.fabrikam.com/path", It.IsAny<CancellationToken>()), Times.Once());
        }

        [Fact]
        public async Task ValidateLogoutRequest_RequestIsRejectedWhenNoMatchingApplicationIsGrantedEndpointPermission()
        {
            // Arrange
            var applications = new[]
            {
                new OpenIddictApplication(),
                new OpenIddictApplication()
            };

            var manager = CreateApplicationManager(mock =>
            {
                mock.Setup(manager => manager.FindByPostLogoutRedirectUriAsync("http://www.fabrikam.com/path", It.IsAny<CancellationToken>()))
                    .Returns(applications.ToAsyncEnumerable());

                mock.Setup(manager => manager.HasPermissionAsync(applications[0], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(false);

                mock.Setup(manager => manager.HasPermissionAsync(applications[1], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(false);
            });

            await using var server = await CreateServerAsync(options =>
            {
                options.Services.AddSingleton(manager);

                options.Configure(options => options.IgnoreEndpointPermissions = false);
            });

            await using var client = await server.CreateClientAsync();

            // Act
            var response = await client.PostAsync("/connect/logout", new OpenIddictRequest
            {
                PostLogoutRedirectUri = "http://www.fabrikam.com/path"
            });

            // Assert
            Assert.Equal(Errors.InvalidRequest, response.Error);
            Assert.Equal("The specified 'post_logout_redirect_uri' parameter is not valid.", response.ErrorDescription);

            Mock.Get(manager).Verify(manager => manager.FindByPostLogoutRedirectUriAsync("http://www.fabrikam.com/path", It.IsAny<CancellationToken>()), Times.Once());
            Mock.Get(manager).Verify(manager => manager.HasPermissionAsync(applications[0], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()), Times.Once());
            Mock.Get(manager).Verify(manager => manager.HasPermissionAsync(applications[1], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()), Times.Once());
        }

        [Fact]
        public async Task ValidateLogoutRequest_RequestIsValidatedWhenMatchingApplicationIsFound()
        {
            // Arrange
            var applications = new[]
            {
                new OpenIddictApplication(),
                new OpenIddictApplication(),
                new OpenIddictApplication()
            };

            var manager = CreateApplicationManager(mock =>
            {
                mock.Setup(manager => manager.FindByPostLogoutRedirectUriAsync("http://www.fabrikam.com/path", It.IsAny<CancellationToken>()))
                    .Returns(applications.ToAsyncEnumerable());

                mock.Setup(manager => manager.HasPermissionAsync(applications[0], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(false);

                mock.Setup(manager => manager.HasPermissionAsync(applications[1], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(true);

                mock.Setup(manager => manager.HasPermissionAsync(applications[2], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(false);
            });

            await using var server = await CreateServerAsync(options =>
            {
                options.Services.AddSingleton(manager);

                options.SetLogoutEndpointUris("/signout");
                options.Configure(options => options.IgnoreEndpointPermissions = false);
            });

            await using var client = await server.CreateClientAsync();

            // Act
            var response = await client.PostAsync("/signout", new OpenIddictRequest
            {
                PostLogoutRedirectUri = "http://www.fabrikam.com/path",
                State = "af0ifjsldkj"
            });

            // Assert
            Assert.Equal("af0ifjsldkj", response.State);

            Mock.Get(manager).Verify(manager => manager.FindByPostLogoutRedirectUriAsync("http://www.fabrikam.com/path", It.IsAny<CancellationToken>()), Times.Once());
            Mock.Get(manager).Verify(manager => manager.HasPermissionAsync(applications[0], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()), Times.Once());
            Mock.Get(manager).Verify(manager => manager.HasPermissionAsync(applications[1], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()), Times.Once());
            Mock.Get(manager).Verify(manager => manager.HasPermissionAsync(applications[2], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()), Times.Never());
        }

        [Theory]
        [InlineData("custom_error", null, null)]
        [InlineData("custom_error", "custom_description", null)]
        [InlineData("custom_error", "custom_description", "custom_uri")]
        [InlineData(null, "custom_description", null)]
        [InlineData(null, "custom_description", "custom_uri")]
        [InlineData(null, null, "custom_uri")]
        [InlineData(null, null, null)]
        public async Task ValidateLogoutRequest_AllowsRejectingRequest(string error, string description, string uri)
        {
            // Arrange
            await using var server = await CreateServerAsync(options =>
            {
                options.EnableDegradedMode();

                options.AddEventHandler<ValidateLogoutRequestContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        context.Reject(error, description, uri);

                        return default;
                    }));
            });

            await using var client = await server.CreateClientAsync();

            // Act
            var response = await client.PostAsync("/connect/logout", new OpenIddictRequest());

            // Assert
            Assert.Equal(error ?? Errors.InvalidRequest, response.Error);
            Assert.Equal(description, response.ErrorDescription);
            Assert.Equal(uri, response.ErrorUri);
        }

        [Fact]
        public async Task ValidateLogoutRequest_AllowsHandlingResponse()
        {
            // Arrange
            await using var server = await CreateServerAsync(options =>
            {
                options.EnableDegradedMode();

                options.AddEventHandler<ValidateLogoutRequestContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        context.Transaction.SetProperty("custom_response", new
                        {
                            name = "Bob le Bricoleur"
                        });

                        context.HandleRequest();

                        return default;
                    }));
            });

            await using var client = await server.CreateClientAsync();

            // Act
            var response = await client.PostAsync("/connect/logout", new OpenIddictRequest());

            // Assert
            Assert.Equal("Bob le Bricoleur", (string) response["name"]);
        }

        [Fact]
        public async Task ValidateLogoutRequest_AllowsSkippingHandler()
        {
            // Arrange
            await using var server = await CreateServerAsync(options =>
            {
                options.EnableDegradedMode();

                options.AddEventHandler<ValidateLogoutRequestContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        context.SkipRequest();

                        return default;
                    }));
            });

            await using var client = await server.CreateClientAsync();

            // Act
            var response = await client.PostAsync("/connect/logout", new OpenIddictRequest());

            // Assert
            Assert.Equal("Bob le Magnifique", (string) response["name"]);
        }

        [Theory]
        [InlineData("custom_error", null, null)]
        [InlineData("custom_error", "custom_description", null)]
        [InlineData("custom_error", "custom_description", "custom_uri")]
        [InlineData(null, "custom_description", null)]
        [InlineData(null, "custom_description", "custom_uri")]
        [InlineData(null, null, "custom_uri")]
        [InlineData(null, null, null)]
        public async Task HandleLogoutRequest_AllowsRejectingRequest(string error, string description, string uri)
        {
            // Arrange
            await using var server = await CreateServerAsync(options =>
            {
                options.EnableDegradedMode();

                options.AddEventHandler<HandleLogoutRequestContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        context.Reject(error, description, uri);

                        return default;
                    }));
            });

            await using var client = await server.CreateClientAsync();

            // Act
            var response = await client.PostAsync("/connect/logout", new OpenIddictRequest());

            // Assert
            Assert.Equal(error ?? Errors.InvalidRequest, response.Error);
            Assert.Equal(description, response.ErrorDescription);
            Assert.Equal(uri, response.ErrorUri);
        }

        [Fact]
        public async Task HandleLogoutRequest_AllowsHandlingResponse()
        {
            // Arrange
            await using var server = await CreateServerAsync(options =>
            {
                options.EnableDegradedMode();

                options.AddEventHandler<HandleLogoutRequestContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        context.Transaction.SetProperty("custom_response", new
                        {
                            name = "Bob le Bricoleur"
                        });

                        context.HandleRequest();

                        return default;
                    }));
            });

            await using var client = await server.CreateClientAsync();

            // Act
            var response = await client.PostAsync("/connect/logout", new OpenIddictRequest());

            // Assert
            Assert.Equal("Bob le Bricoleur", (string) response["name"]);
        }

        [Fact]
        public async Task HandleLogoutRequest_AllowsSkippingHandler()
        {
            // Arrange
            await using var server = await CreateServerAsync(options =>
            {
                options.EnableDegradedMode();

                options.AddEventHandler<HandleLogoutRequestContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        context.SkipRequest();

                        return default;
                    }));
            });

            await using var client = await server.CreateClientAsync();

            // Act
            var response = await client.PostAsync("/connect/logout", new OpenIddictRequest());

            // Assert
            Assert.Equal("Bob le Magnifique", (string) response["name"]);
        }

        [Fact]
        public async Task ApplyLogoutResponse_AllowsHandlingResponse()
        {
            // Arrange
            await using var server = await CreateServerAsync(options =>
            {
                options.EnableDegradedMode();

                options.AddEventHandler<ApplyLogoutResponseContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        context.Transaction.SetProperty("custom_response", new
                        {
                            name = "Bob le Bricoleur"
                        });

                        context.HandleRequest();

                        return default;
                    }));
            });

            await using var client = await server.CreateClientAsync();

            // Act
            var response = await client.PostAsync("/connect/logout", new OpenIddictRequest());

            // Assert
            Assert.Equal("Bob le Bricoleur", (string) response["name"]);
        }

        [Fact]
        public async Task ApplyLogoutResponse_ResponseContainsCustomParameters()
        {
            // Arrange
            await using var server = await CreateServerAsync(options =>
            {
                options.EnableDegradedMode();

                options.AddEventHandler<ApplyLogoutResponseContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        context.Response["custom_parameter"] = "custom_value";
                        context.Response["parameter_with_multiple_values"] = new[]
                        {
                            "custom_value_1",
                            "custom_value_2"
                        };

                        return default;
                    }));
            });

            await using var client = await server.CreateClientAsync();

            // Act
            var response = await client.PostAsync("/connect/logout", new OpenIddictRequest
            {
                PostLogoutRedirectUri = "http://www.fabrikam.com/path"
            });

            // Assert
            Assert.Equal("custom_value", (string) response["custom_parameter"]);
            Assert.Equal(new[] { "custom_value_1", "custom_value_2" }, (string[]) response["parameter_with_multiple_values"]);
        }

        [Fact]
        public async Task ApplyLogoutResponse_DoesNotSetStateWhenUserIsNotRedirected()
        {
            // Arrange
            await using var server = await CreateServerAsync(options =>
            {
                options.EnableDegradedMode();
                options.SetLogoutEndpointUris("/signout");
            });

            await using var client = await server.CreateClientAsync();

            // Act
            var response = await client.PostAsync("/signout", new OpenIddictRequest
            {
                State = "af0ifjsldkj"
            });

            // Assert
            Assert.Null(response.State);
        }

        [Fact]
        public async Task ApplyLogoutResponse_FlowsStateWhenRedirectUriIsUsed()
        {
            // Arrange
            await using var server = await CreateServerAsync(options =>
            {
                options.EnableDegradedMode();
                options.SetLogoutEndpointUris("/signout");
            });

            await using var client = await server.CreateClientAsync();

            // Act
            var response = await client.PostAsync("/signout", new OpenIddictRequest
            {
                PostLogoutRedirectUri = "http://www.fabrikam.com/path",
                State = "af0ifjsldkj"
            });

            // Assert
            Assert.Equal("af0ifjsldkj", response.State);
        }

        [Fact]
        public async Task ApplyLogoutResponse_DoesNotOverrideStateSetByApplicationCode()
        {
            // Arrange
            await using var server = await CreateServerAsync(options =>
            {
                options.EnableDegradedMode();
                options.SetLogoutEndpointUris("/signout");

                options.AddEventHandler<ApplyLogoutResponseContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        context.Response.State = "custom_state";

                        return default;
                    }));
            });

            await using var client = await server.CreateClientAsync();

            // Act
            var response = await client.PostAsync("/signout", new OpenIddictRequest
            {
                PostLogoutRedirectUri = "http://www.fabrikam.com/path",
                State = "af0ifjsldkj"
            });

            // Assert
            Assert.Equal("custom_state", response.State);
        }
    }
}
