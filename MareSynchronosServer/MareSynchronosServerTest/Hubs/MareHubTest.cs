using MareSynchronosServer.Data;
using MareSynchronosServer.Hubs;
using MareSynchronosServer.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace MareSynchronosServerTest.Hubs {
    public class MareHubTest {

        [Test]
        public async Task Disconnect_QueryReturnsCorrectResult_Test() {
            var options = new DbContextOptionsBuilder<MareDbContext>()
                .UseInMemoryDatabase(databaseName: "mare").Options;

            using var context = new MareDbContext(options);
            context.Users.Add(new User() { UID = "User1", IsModerator = false, IsAdmin = false, CharacterIdentification = "Ident1" });
            context.Users.Add(new User() { UID = "User2", IsModerator = false, IsAdmin = false, CharacterIdentification = "Ident2" });
            context.Users.Add(new User() { UID = "User3", IsModerator = false, IsAdmin = false, CharacterIdentification = "Ident3" });
            context.Users.Add(new User() { UID = "User4", IsModerator = false, IsAdmin = false, CharacterIdentification = "Ident4" });
            context.Users.Add(new User() { UID = "User5", IsModerator = false, IsAdmin = false, CharacterIdentification = "Ident5" });
            context.Users.Add(new User() { UID = "User6", IsModerator = false, IsAdmin = false, CharacterIdentification = "Ident6" });

            // Valid pairs
            context.ClientPairs.Add(new ClientPair() { UserUID = "User1", OtherUserUID = "User2", IsPaused = false });
            context.ClientPairs.Add(new ClientPair() { UserUID = "User2", OtherUserUID = "User1", IsPaused = false });
            context.ClientPairs.Add(new ClientPair() { UserUID = "User1", OtherUserUID = "User3", IsPaused = false });
            context.ClientPairs.Add(new ClientPair() { UserUID = "User3", OtherUserUID = "User1", IsPaused = false });

            // Other user paired but user not paired with current user
            context.ClientPairs.Add(new ClientPair() { UserUID = "User4", OtherUserUID = "User1", IsPaused = false });

            // Valid pair but user paused
            context.ClientPairs.Add(new ClientPair() { UserUID = "User1", OtherUserUID = "User5", IsPaused = true });
            context.ClientPairs.Add(new ClientPair() { UserUID = "User5", OtherUserUID = "User1", IsPaused = false });

            // Valid pair but other user paused
            context.ClientPairs.Add(new ClientPair() { UserUID = "User1", OtherUserUID = "User6", IsPaused = false });
            context.ClientPairs.Add(new ClientPair() { UserUID = "User6", OtherUserUID = "User1", IsPaused = true });

            // Non existant user
            context.ClientPairs.Add(new ClientPair() { UserUID = "User99", OtherUserUID = "User1", IsPaused = false });

            // Non-related data
            context.ClientPairs.Add(new ClientPair() { UserUID = "User6", OtherUserUID = "User4", IsPaused = true });
            context.ClientPairs.Add(new ClientPair() { UserUID = "User4", OtherUserUID = "User3", IsPaused = false });
            context.ClientPairs.Add(new ClientPair() { UserUID = "User3", OtherUserUID = "User2", IsPaused = false });

            context.SaveChanges();

            var clientContextMock = new Mock<HubCallerContext>();
            var claimMock = new Mock<ClaimsPrincipal>();
            var claim = new Claim(ClaimTypes.NameIdentifier, "User1");
            claimMock.SetupGet(x => x.Claims).Returns(new List<Claim>() { claim });
            clientContextMock.SetupGet(x => x.User).Returns(claimMock.Object);

            var clientsMock = new Mock<IHubCallerClients>();
            var clientProxyMock = new Mock<IClientProxy>();
            clientsMock.Setup(x => x.Users(It.IsAny<IReadOnlyList<string>>())).Returns(clientProxyMock.Object);            

            var hub = new MareHub(context, new Mock<ILogger<MareHub>>().Object, null, new Mock<IConfiguration>().Object, new Mock<IHttpContextAccessor>().Object);


            hub.Clients = clientsMock.Object;
            hub.Context = clientContextMock.Object;

            await hub.OnDisconnectedAsync(new Exception("Test Exception"));

            clientsMock.Verify(x => x.Users(It.Is<IReadOnlyList<string>>(x => x.Count() == 2 && x[0] == "User2" && x[1] == "User3")), Times.Once);
            clientProxyMock.Verify(x => x.SendCoreAsync(It.IsAny<string>(), It.Is<object[]>(o => (string)o[0] == "Ident1"), It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
