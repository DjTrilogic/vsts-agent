using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Listener;
using Microsoft.VisualStudio.Services.Agent.Listener.Configuration;
using Moq;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Extensions;

namespace Microsoft.VisualStudio.Services.Agent.Tests.Listener
{
    public sealed class AgentL0
    {
        private Mock<IConfigurationManager> _configurationManager;
        private Mock<IMessageListener> _messageListener;
        private Mock<IWorkerManager> _workerManager;
        private Mock<IAgentServer> _agentServer;
        private Mock<ITerminal> _term;

        public AgentL0()
        {
            _configurationManager = new Mock<IConfigurationManager>();
            _messageListener = new Mock<IMessageListener>();
            _workerManager = new Mock<IWorkerManager>();
            _agentServer = new Mock<IAgentServer>();            
            _term = new Mock<ITerminal>();
        }

        private JobRequestMessage CreateJobRequestMessage(string jobName)
        {
            TaskOrchestrationPlanReference plan = new TaskOrchestrationPlanReference();
            TimelineReference timeline = null;
            JobEnvironment environment = new JobEnvironment();
            List<TaskInstance> tasks = new List<TaskInstance>();
            Guid JobId = Guid.NewGuid();
            var jobRequest = new JobRequestMessage(plan, timeline, JobId, jobName, environment, tasks);
            return jobRequest;
        }

        private JobCancelMessage CreateJobCancelMessage()
        {
            var message = new JobCancelMessage(Guid.NewGuid(), TimeSpan.FromSeconds(0));
            return message;
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Agent")]
        //process 2 new job messages, and one cancel message
        public async void TestRunAsync()
        {
            using (var hc = new TestHostContext(this))
            using (var tokenSource = new CancellationTokenSource())
            {
                //Arrange
                var agent = new Agent.Listener.Agent();
                agent.TokenSource = tokenSource;
                hc.SetSingleton<IConfigurationManager>(_configurationManager.Object);
                hc.SetSingleton<IMessageListener>(_messageListener.Object);
                hc.SetSingleton<IWorkerManager>(_workerManager.Object);
                hc.SetSingleton<IAgentServer>(_agentServer.Object);
                agent.Initialize(hc);
                var settings = new AgentSettings
                {
                    PoolId = 43242
                };
                var taskAgentSession = new TaskAgentSession
                {
                    //SessionId = Guid.NewGuid() //we use reflection to achieve this, because "set" is internal
                };
                PropertyInfo sessionIdProperty = taskAgentSession.GetType().GetProperty("SessionId", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Assert.NotNull(sessionIdProperty);
                sessionIdProperty.SetValue(taskAgentSession, Guid.NewGuid());

                var arMessages = new TaskAgentMessage[]
                    {
                        new TaskAgentMessage
                        {
                            Body = JsonUtility.ToString(CreateJobRequestMessage("job1")),
                            MessageId = 4234,
                            MessageType = JobRequestMessage.MessageType
                        },
                        new TaskAgentMessage
                        {
                            Body = JsonUtility.ToString(CreateJobCancelMessage()),
                            MessageId = 4235,
                            MessageType = JobCancelMessage.MessageType
                        },
                        new TaskAgentMessage
                        {
                            Body = JsonUtility.ToString(CreateJobRequestMessage("last_job")),
                            MessageId = 4236,
                            MessageType = JobRequestMessage.MessageType
                        }
                    };
                var messages = new Queue<TaskAgentMessage>(arMessages);
                var signalWorkerComplete = new SemaphoreSlim(0, 1);
                _configurationManager.Setup(x => x.LoadSettings())
                    .Returns(settings);
                _configurationManager.Setup(x => x.IsConfigured())
                    .Returns(true);
                _configurationManager.Setup(x => x.EnsureConfiguredAsync())
                    .Returns(Task.CompletedTask);
                _messageListener.Setup(x => x.CreateSessionAsync(It.IsAny<CancellationToken>()))
                    .Returns(Task.FromResult<bool>(true));
                _messageListener.Setup(x => x.Session)
                    .Returns(taskAgentSession);
                _messageListener.Setup(x => x.GetNextMessageAsync(It.IsAny<CancellationToken>()))
                    .Returns(async () =>
                        {
                            if (0 == messages.Count)
                            {
                                await Task.Delay(2000, tokenSource.Token);
                                throw new TimeoutException();
                            }

                            return messages.Dequeue();
                        });
                _messageListener.Setup(x => x.DeleteSessionAsync())
                    .Returns(Task.CompletedTask);
                _workerManager.Setup(x => x.Run(It.IsAny<JobRequestMessage>()))
                    .Callback((JobRequestMessage m) =>
                       {
                            //last job starts the task
                            if (m.JobName.Equals("last_job"))
                           {
                               signalWorkerComplete.Release();
                           }
                       }
                    );
                _workerManager.Setup(x => x.Cancel(It.IsAny<JobCancelMessage>()));
                _agentServer.Setup(x => x.DeleteAgentMessageAsync(settings.PoolId, arMessages[0].MessageId, taskAgentSession.SessionId, It.IsAny<CancellationToken>()))
                    .Returns((Int32 poolId, Int64 messageId, Guid sessionId, CancellationToken cancellationToken) =>
                   {
                       return Task.CompletedTask;
                   });

                //Act
                var parser = new CommandLineParser(hc);
                parser.Parse(new string[] { "" });
                Task agentTask = agent.ExecuteCommand(parser);

                //Assert
                //wait for the agent to run one job
                if (!await signalWorkerComplete.WaitAsync(2000))
                {
                    Assert.True(false, $"{nameof(_messageListener.Object.GetNextMessageAsync)} was not invoked.");
                }
                else
                {
                    //Act
                    tokenSource.Cancel(); //stop Agent

                    //Assert
                    Task[] taskToWait2 = { agentTask, Task.Delay(2000) };
                    //wait for the Agent to exit
                    await Task.WhenAny(taskToWait2);

                    Assert.True(agentTask.IsCompleted, $"{nameof(agent.ExecuteCommand)} timed out.");
                    Assert.True(!agentTask.IsFaulted, agentTask.Exception?.ToString());
                    Assert.True(agentTask.IsCanceled);

                    _workerManager.Verify(x => x.Run(It.IsAny<JobRequestMessage>()), Times.AtLeast(2),
                         $"{nameof(_workerManager.Object.Run)} was not invoked.");
                    _workerManager.Verify(x => x.Cancel(It.IsAny<JobCancelMessage>()), Times.Once(),
                        $"{nameof(_workerManager.Object.Cancel)} was not invoked.");
                    _messageListener.Verify(x => x.GetNextMessageAsync(It.IsAny<CancellationToken>()), Times.AtLeast(arMessages.Length));
                    _messageListener.Verify(x => x.CreateSessionAsync(It.IsAny<CancellationToken>()), Times.Once());
                    _messageListener.Verify(x => x.DeleteSessionAsync(), Times.Once());
                    _agentServer.Verify(x => x.DeleteAgentMessageAsync(settings.PoolId, arMessages[0].MessageId, taskAgentSession.SessionId, It.IsAny<CancellationToken>()), Times.Once());
                }
            }
        }

        public static TheoryData<string[], bool, Times> RunAsServiceTestData = new TheoryData<string[], bool, Times>()
                                                                    {
                                                                        // staring with run command, configured as run as service, should start the agent
                                                                        { new [] { "run" }, true, Times.Once() },
                                                                        // starting with no argument, configured as run as service, should not start agent
                                                                        { new string[] { }, true, Times.Never() },
                                                                        // starting with no argument, configured not to run as service, should start agent interactively
                                                                        { new string[] { }, false, Times.Once() }
                                                                    };
        [Theory]
        [MemberData("RunAsServiceTestData")]
        [Trait("Level", "L0")]
        [Trait("Category", "Agent")]
        public async void TestExecuteCommandForRunAsService(string[] args, bool configureAsService, Times expectedTimes)
        {
            using (var hc = new TestHostContext(this))
            {
                hc.SetSingleton<IConfigurationManager>(_configurationManager.Object);
                hc.SetSingleton<IMessageListener>(_messageListener.Object);

                CommandLineParser clp = new CommandLineParser(hc);
                clp.Parse(args);

                _configurationManager.Setup(x => x.IsConfigured()).Returns(true);
                _configurationManager.Setup(x => x.LoadSettings())
                    .Returns(new AgentSettings { RunAsService = configureAsService });
                _messageListener.Setup(x => x.CreateSessionAsync(It.IsAny<CancellationToken>()))
                    .Returns(Task.FromResult(false));

                var agent = new Agent.Listener.Agent();
                agent.Initialize(hc);
                agent.TokenSource = new CancellationTokenSource();
                await agent.ExecuteCommand(clp);

                _messageListener.Verify(x => x.CreateSessionAsync(It.IsAny<CancellationToken>()), expectedTimes);
            }
        }
    }
}
