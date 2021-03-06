﻿using EventNext.Proxy;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace EventNext
{
    public class EventCenter : IDisposable
    {
        static EventCenter()
        {
            Watch = new System.Diagnostics.Stopwatch();

            Watch.Start();
        }

        public const string ACTOR_TAG = "ACTOR";

        public const string ACTOR_NULL_TAG = "___NULL_ACTOR";

        public EventCenter()
        {
            LogLevel = LogType.Error;
            ActorFreeTime = 60;
            mFreeTimer = new System.Threading.Timer(OnFreeActor, null, 1000 * 30, 1000 * 30);
        }

        private long mID = 0;

        private System.Threading.Timer mFreeTimer;

        private ConcurrentDictionary<string, EventActionHandler> mActionHadlers = new ConcurrentDictionary<string, EventActionHandler>();

        private ConcurrentDictionary<Type, ActorProxyCollection> mActorProxyMap = new ConcurrentDictionary<Type, ActorProxyCollection>();

        private ConcurrentDictionary<Type, ActorCollection> mActorsCollection = new ConcurrentDictionary<Type, ActorCollection>();

        private ConcurrentDictionary<string, Object> mProperties = new ConcurrentDictionary<string, object>();

        protected virtual object CreateController(Type type)
        {
            object result;
            if (ServiceInstance != null)
            {
                Events.EventServiceInstanceArgs e = new Events.EventServiceInstanceArgs(this, type);
                ServiceInstance(this, e);
                result = e.Service ?? Activator.CreateInstance(type);
            }
            result = Activator.CreateInstance(type);
            if (result is IController controller)
                controller.Initialize(this);
            return result;
        }

        private void OnFreeActor(object state)
        {
            mFreeTimer.Change(-1, -1);
            try
            {
                foreach (var item in mActorsCollection.Values)
                {
                    item.Free(this);
                }
            }
            catch (Exception e_)
            {
                if (EnabledLog(LogType.Error))
                    Log(LogType.Error, $"Timer free actor error {e_.Message}@{e_.StackTrace}");
            }
            finally
            {
                mFreeTimer.Change(1000 * 30, 1000 * 30);
            }
        }

        public int NextQueueWaits { get; set; } = 5;

        public virtual long GetInputID()
        {
            return System.Threading.Interlocked.Increment(ref mID);
        }

        public NextQueueGroup InputNextQueue { get; set; } = new NextQueueGroup();

        public NextQueueGroup OutputNextQueue { get; set; } = new NextQueueGroup();

        public static System.Diagnostics.Stopwatch Watch { get; private set; }

        public LogType LogLevel { get; set; }

        public int ActorFreeTime { get; set; }

        public event EventHandler<Events.EventLogArgs> LogOutput;

        public bool EnabledLog(LogType type)
        {
            return (int)(this.LogLevel) <= (int)type;
        }

        public void Log(LogType logType, string message)
        {
            try
            {
                LogOutput?.Invoke(this, new Events.EventLogArgs(this, logType, message));
            }
            catch
            {

            }
        }

        private void OnRegister(ServiceAttribute attribute, Type type, object controller)
        {
            foreach (Type itype in attribute.Types)
            {
                if (!itype.IsInterface)
                {
                    continue;
                }
                if (type.GetInterface(itype.Name) == null)
                {
                    continue;
                }
                string serviceName = (attribute.Name ?? itype.Name);
                string url = "/" + serviceName + "/";
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    try
                    {
                        if (string.Compare("Equals", method.Name, true) == 0
                      || string.Compare("GetHashCode", method.Name, true) == 0
                      || string.Compare("GetType", method.Name, true) == 0
                      || string.Compare("ToString", method.Name, true) == 0 || method.Name.IndexOf("set_") >= 0
                      || method.Name.IndexOf("get_") >= 0)
                            continue;
                        var resultType = method.ReturnType;
                        if (resultType.BaseType == typeof(Task) || resultType == typeof(Task))
                        {
                            Type serviceType = controller.GetType();
                            if (!mActorsCollection.TryGetValue(serviceType, out ActorCollection serviceCollection))
                            {
                                serviceCollection = new ActorCollection(serviceType);
                                mActorsCollection[serviceType] = serviceCollection;
                                mActorsCollection[itype] = serviceCollection;
                            }
                            ActionAttribute aa = method.GetCustomAttribute<ActionAttribute>(false);

                            var actionUrl = url + (aa == null ? method.Name : aa.Name);
                            if (mActionHadlers.TryGetValue(actionUrl, out EventActionHandler handler))
                            {
                                Log(LogType.Warring, $"{itype.Name}->{type.Name}.{method.Name} action already exists, can add ActionAttribute on the method");
                            }
                            else
                            {
                                handler = new EventActionHandler(type, method, controller);
                                handler.ServiceName = serviceName;
                                handler.ActionName = (aa == null ? method.Name : aa.Name);
                                handler.SingleInstance = attribute.SingleInstance;
                                handler.Actors = serviceCollection;
                                handler.Interface = itype;
                                mActionHadlers[actionUrl] = handler;
                                string threadUniqueID = "";
                                foreach (var index in handler.ThreadUniqueID)
                                {
                                    if (!string.IsNullOrEmpty(threadUniqueID))
                                        threadUniqueID += ".";
                                    threadUniqueID += handler.Parameters[index].ParameterInfo.Name;
                                }
                                Log(LogType.Info, $"Register {itype.Name}->{type.Name}@{method.Name} to {actionUrl}[ThreadType:{handler.ThreadType}|UniqueID:{threadUniqueID}]");
                            }
                        }
                        else
                        {
                            Log(LogType.Error, $"Register {itype.Name}->{type.Name}.{method.Name} error, method result type not a Task!");
                        }
                    }
                    catch (Exception e_)
                    {
                        Log(LogType.Error, $"Register {itype.Name}->{type.Name}@{method.Name} action error {e_.Message}@{e_.StackTrace}");
                    }
                }
            }
        }

        public event EventHandler<Events.EventServiceInstanceArgs> ServiceInstance;

        public Object this[string name]
        {
            get
            {
                return mProperties[name];
            }
            set
            {
                mProperties[name] = value;
            }
        }

        public T GetProperty<T>(string name)
        {
            return (T)this[name];
        }

        public void Register(object service)
        {

            Type type = service.GetType();
            var attribute = type.GetCustomAttribute<ServiceAttribute>(false);
            if (attribute != null)
            {
                OnRegister(attribute, type, service);
            }
            else
            {
                Log(LogType.Warring, $"{type.Name} no controller attribute");
            }
        }

        public void Register(params Assembly[] assemblies)
        {
            foreach (Assembly item in assemblies)
            {
                foreach (Type type in item.GetTypes())
                {
                    try
                    {
                        if (type.IsPublic && !type.IsAbstract && type.IsClass)
                        {
                            var attribute = type.GetCustomAttribute<ServiceAttribute>(false);
                            if (attribute != null)
                            {
                                object controller = CreateController(type);
                                OnRegister(attribute, type, controller);
                            }
                        }
                    }
                    catch (Exception e_)
                    {
                        Log(LogType.Error, $"Register {type.Name} error {e_.Message}@{e_.StackTrace}");
                    }
                }
            }
        }

        public EventActionHandler GetActionHandler(string url)
        {
            mActionHadlers.TryGetValue(url, out EventActionHandler handler);
            return handler;
        }

        public Task<IEventOutput> Execute(IEventInput input)
        {
            EventCompleted eventCompleted = new EventCompleted();
            Execute(input, eventCompleted);
            return eventCompleted.GetTask();
        }

        public void Execute(IEventInput input, IEventCompleted callBackEvent)
        {
            if (input.ID == 0)
                input.ID = GetInputID();
            EventOutput output = new EventOutput();
            output.Token = input.Token;
            output.ID = input.ID;
            output.EventError = EventError.Success;
            EventActionHandler handler = GetActionHandler(input.EventPath);
            if (handler == null)
            {
                output.EventError = EventError.NotFound;
                output.Data = new object[] { $"Process event error {input.EventPath} not found!" };
                if (EnabledLog(LogType.Warring))
                {
                    Log(LogType.Warring, $"{input.Token} Process event error {input.EventPath} not found!");
                }
                callBackEvent.Completed(output);
            }
            else
            {
                try
                {
                    OnExecute(input, output, handler, callBackEvent);
                }
                catch (Exception e_)
                {
                    output.EventError = EventError.InnerError;
                    output.Data = new object[] { $"Process event {input.EventPath} error {e_.Message}" };
                    if (EnabledLog(LogType.Error))
                        Log(LogType.Error, $"{input.Token} process event {input.EventPath} error {e_.Message}@{e_.StackTrace}");
                    callBackEvent.Completed(output);
                }
            }

        }

        private void OnExecute(IEventInput input, EventOutput output, EventActionHandler handler, IEventCompleted callBackEvent)
        {
            var actorID = input.Properties?[ACTOR_TAG];
            string actorPath = null;
            object controller = null;
            NextQueue nextQueue = null;
            if (string.IsNullOrEmpty(actorID))
            {
                nextQueue = this.InputNextQueue.Next(this.NextQueueWaits);
                if (EnabledLog(LogType.Debug))
                {
                    Log(LogType.Debug, $"{input.Token} Process event {input.EventPath}");
                }
                controller = handler.Controller;
                if (handler.ThreadType == ThreadType.SingleQueue)
                {
                    nextQueue = handler.GetNextQueue(input.Data);
                }
                if (!handler.SingleInstance)
                {
                    controller = CreateController(handler.ControllerType);
                }
            }
            else
            {
                ActorCollection.ActorCollectionItem item;
                item = handler.Actors.Get(actorID);
                if (item == null)
                {
                    actorPath = "/" + handler.ServiceName + "/" + actorID;
                    if (EnabledLog(LogType.Debug))
                        Log(LogType.Debug, $"{input.Token} {handler.ControllerType.Name}@{actorPath} create actor");
                    item = new ActorCollection.ActorCollectionItem();
                    item.ActorID = actorID;
                    item.Actor = CreateController(handler.Actors.ServiceType);
                    item.ServiceName = handler.ServiceName;
                    item.Interface = handler.Interface;
                    item = handler.Actors.Set(actorID, item);
                    item.TimeOut = EventCenter.Watch.ElapsedMilliseconds + ActorFreeTime * 1000;
                    controller = item.Actor;
                    if (controller is IActorState state)
                    {
                        state.ActorPath = actorPath;
                        state.EventCenter = this;
                        state.EventPath = input.EventPath;
                        state.Token = input.Token;
                        state.ActorInit(actorID);
                        if (EnabledLog(LogType.Debug))
                            Log(LogType.Debug, $"{input.Token} {handler.ControllerType.Name}@{actorPath} actor initialized");
                    }
                }
                else
                {
                    item.TimeOut = EventCenter.Watch.ElapsedMilliseconds + ActorFreeTime * 1000;
                    controller = item.Actor;
                    if (controller is IActorState state)
                    {
                        state.EventPath = input.EventPath;
                        state.Token = input.Token;
                    }
                }
                nextQueue = item.NextQueue;
                if (EnabledLog(LogType.Debug))
                {
                    Log(LogType.Debug, $"{input.Token} Process event {input.EventPath} in /{item.ServiceName}/{item.ActorID} actor");
                }
            }
            EventActionHandlerContext context = new EventActionHandlerContext(this, input, handler, controller, nextQueue);
            context.Execute(output, callBackEvent);
        }

        private ActorProxyCollection GetActorProxy(Type type)
        {
            if (!mActorProxyMap.TryGetValue(type, out ActorProxyCollection actorProxy))
            {
                lock (mActorProxyMap)
                {
                    if (!mActorProxyMap.TryGetValue(type, out actorProxy))
                    {
                        actorProxy = new ActorProxyCollection();
                        mActorProxyMap[type] = actorProxy;
                    }
                }
            }
            return actorProxy;
        }

        private T CreateActorProxy<T>(string actor)
        {
            Type type = typeof(T);
            var actorProxy = GetActorProxy(type);
            var result = actorProxy.Get(actor);
            if (result == null)
            {
                lock (actorProxy)
                {
                    result = actorProxy.Get(actor);
                    if (result == null)
                    {
                        result = (EventDispatchProxy)CreateProxy<T>(actor);
                        actorProxy.Set(actor, result);
                    }
                }
            }
            return (T)(object)result;
        }

        private object CreateProxy<T>(string actor = null)
        {
            object result = DispatchProxy.Create<T, Proxy.EventDispatchProxy>();
            EventDispatchProxy dispatch = ((EventDispatchProxy)result);
            dispatch.EventCenter = this;
            dispatch.Actor = actor;
            dispatch.Type = typeof(T);
            dispatch.InitHandlers();
            if (EnabledLog(LogType.Debug))
                Log(LogType.Debug, $"create {dispatch.Type.Name}@{actor} event dispatch proxy successed!");
            return result;
        }

        public T Create<T>(string actor = null)
        {
            Type type = typeof(T);
            if (!type.IsInterface)
            {
                var error = new ENException($"{type.Name} not a interface!");
                error.EventError = EventError.NotSupport;
                throw error;
            }
            if (string.IsNullOrEmpty(actor))
            {
                actor = ACTOR_NULL_TAG;
            }
            return CreateActorProxy<T>(actor);
        }

        public void ActorFlush<T>(string actor = null)
        {
            ActorFlush(typeof(T), actor);
        }

        public void ActorFlush(Type type, string actor)
        {
            if (mActorsCollection.TryGetValue(type, out ActorCollection service))
            {
                service.Flush(this, actor);
            }
        }

        public void Dispose()
        {
            mActionHadlers.Clear();
            mActorProxyMap.Clear();
            foreach (var item in mActorsCollection.Values)
            {
                item.Free(this, true);
            }
            mActorsCollection.Clear();
            if (mFreeTimer != null)
                mFreeTimer.Dispose();
        }

        public Task<EventLog> ReadEvent(IActorState actor, string eventid)
        {
            return EventLogHandler?.Read(actor, eventid);
        }

        public async Task<string> WriteEvent(IActorState actor, string eventid, string parentEventid, object data)
        {
            if (EventLogHandler != null)
            {
                var result = await EventLogHandler.Write(actor,
                    new EventLog { Data = data, DateTime = DateTime.Now, ActorPath = actor.ActorPath, EventPath = actor.EventPath, EventID = eventid, ParentEventID = parentEventid });
                return result;
            }
            return null;
        }

        public IEventLogHandler EventLogHandler { get; set; }
    }

    public static class TaskExten
    {

        public static Task<T> ToTask<T>(this T result)
        {
            return Task.FromResult<T>(result);
        }
    }
}
