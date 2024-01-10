﻿using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using static System.Collections.Specialized.BitVector32;

namespace ReactorData.Implementation;

class ModelContext : IModelContext
{
    #region Operations
    abstract class Operation();

    abstract class OperationPending() : Operation;

    abstract class OperationSingle(IEntity entity) : OperationPending
    {
        public IEntity Entity { get; } = entity;
    }

    class OperationAdd(IEntity entity) : OperationSingle(entity);

    class OperationUpdate(IEntity entity) : OperationSingle(entity);

    class OperationDelete(IEntity entity) : OperationSingle(entity);

    class OperationAddRange(IEnumerable<IEntity> entities) : OperationPending
    {
        public IEnumerable<IEntity> Entities { get; } = entities;
    }
    class OperationUpdateRange(IEnumerable<IEntity> entities) : OperationPending
    {
        public IEnumerable<IEntity> Entities { get; } = entities;
    }

    class OperationDeleteRange(IEnumerable<IEntity> entities) : OperationPending
    {
        public IEnumerable<IEntity> Entities { get; } = entities;
    }

    class OperationFetch(Func<IStorage, Task<IEnumerable<IEntity>>> loadFunction, Func<IEntity, IEntity, bool>? compareFunc = null) : Operation
    {
        public Func<IStorage, Task<IEnumerable<IEntity>>> LoadFunction { get; } = loadFunction;
        public Func<IEntity, IEntity, bool>? CompareFunc { get; } = compareFunc;
    }

    class OperationSave() : Operation;

    class OperationFlush(AsyncAutoResetEvent signal) : Operation
    {
        public AsyncAutoResetEvent Signal { get; } = signal;
    }
    #endregion

    private readonly ConcurrentDictionary<Type, Dictionary<object, IEntity>> _sets = [];

    private readonly List<OperationPending> _pendingQueue = [];
    private readonly ConcurrentDictionary<IEntity, bool> _pendingInserts = [];
    private readonly ConcurrentDictionary<object, IEntity> _pendingUpdates = [];
    private readonly ConcurrentDictionary<object, IEntity> _pendingDeletes = [];

    private readonly ActionBlock<Operation> _operationsBlock;

    private readonly IStorage? _storage;

    private readonly ConcurrentDictionary<Type, List<WeakReference<IObservableQuery>>> _queries = [];

    private readonly SemaphoreSlim _notificationSemaphore = new(1);

    public ModelContext(IServiceProvider serviceProvider, ModelContextOptions options)
    {
        _operationsBlock = new ActionBlock<Operation>(DoWork);
        _storage = serviceProvider.GetService<IStorage>();

        Options = options;
        Options.ConfigureContext?.Invoke(this);
    }

    public Action<Exception>? OnError { get; set; }

    public ModelContextOptions Options { get; }

    public EntityStatus GetEntityStatus(IEntity entity)
    {
        if (_pendingInserts.TryGetValue(entity, out var _))
        {
            return EntityStatus.Added;
        }
        
        var key = entity.GetKey();

        if (key != null)
        {
            if (_pendingUpdates.TryGetValue(key, out var _))
            {
                return EntityStatus.Updated;
            }
            if (_pendingDeletes.TryGetValue(key, out var _))
            {
                return EntityStatus.Deleted;
            }

            var set = _sets.GetOrAdd(entity.GetType(), []);
            if (set.TryGetValue(key, out var _))
            {
                return EntityStatus.Attached;
            }
        }

        return EntityStatus.Detached;
    }

    private async Task DoWork(Operation operation)
    {
        try
        {
            await InternalDoWork(operation);
        }
        catch (Exception ex)
        {
            try
            {
                OnError?.Invoke(ex);
            }
            catch { }
        }
    }

    private async Task InternalDoWork(Operation operation)
    {
        switch (operation)
        {
            case OperationAdd operationAdd:
                if (_pendingInserts.TryAdd(operationAdd.Entity, true))
                {
                    _pendingQueue.Add(operationAdd);

                    NotifyChanges(operationAdd.Entity.GetType());
                }
                break;
            case OperationUpdate operationUpdate:
                {
                    var entityKey = operationUpdate.Entity.GetKey().EnsureNotNull();

                    if (_pendingDeletes.TryRemove(entityKey, out _))
                    {
                        _pendingQueue.RemoveFirst(_ => _ is OperationDelete operationDelete && operationDelete.Entity == operationUpdate.Entity);
                        NotifyChanges(operationUpdate.Entity.GetType());
                    }

                    if (_pendingUpdates.TryAdd(entityKey, operationUpdate.Entity))
                    {
                        _pendingQueue.Add(operationUpdate);
                        NotifyChanges(operationUpdate.Entity.GetType(), operationUpdate.Entity);
                    }
                }
                break;
            case OperationDelete operationRemove:
                {
                    var entityKey = operationRemove.Entity.GetKey().EnsureNotNull();
                    if (_pendingUpdates.TryGetValue(entityKey, out _))
                    {
                        _pendingQueue.RemoveFirst(_ => _ is OperationUpdate operationUpdate && operationUpdate.Entity == operationRemove.Entity);
                    }

                    if (_pendingDeletes.TryAdd(entityKey, operationRemove.Entity))
                    {
                        _pendingQueue.Add(operationRemove);
                        NotifyChanges(operationRemove.Entity.GetType());
                    }
                }
                break;
            case OperationAddRange operationAddRange:
                {
                    HashSet<Type> queryTypesToNofity = [];
                    List<IEntity> entitiesToAdd = [];
                    foreach (var entity in operationAddRange.Entities)
                    {
                        if (_pendingInserts.TryAdd(entity, true))
                        {
                            entitiesToAdd.Add(entity);

                            if (!queryTypesToNofity.Contains(entity.GetType()))
                            {
                                queryTypesToNofity.Add(entity.GetType());
                            }
                        }
                    }

                    _pendingQueue.Add(new OperationAddRange(entitiesToAdd));

                    foreach (var queryTypeToNofity in queryTypesToNofity)
                    {
                        NotifyChanges(queryTypeToNofity);
                    }
                }

                break;
            case OperationDeleteRange operationDeleteRange:
                {
                    HashSet<Type> queryTypesToNofity = [];
                    List<IEntity> entitiesToDelete = [];

                    foreach (var entity in operationDeleteRange.Entities)
                    {
                        var entityKey = entity.GetKey().EnsureNotNull();

                        if (_pendingUpdates.TryGetValue(entityKey.EnsureNotNull(), out _))
                        {
                            _pendingQueue.RemoveFirst(_ => _ is OperationUpdate operationUpdate && operationUpdate.Entity == entity);
                        }

                        if (_pendingDeletes.TryAdd(entityKey, entity))
                        {
                            entitiesToDelete.Add(entity);

                            if (!queryTypesToNofity.Contains(entity.GetType()))
                            {
                                queryTypesToNofity.Add(entity.GetType());
                            }
                        }
                    }

                    _pendingQueue.Add(new OperationDeleteRange(entitiesToDelete));

                    foreach (var queryTypeToNofity in queryTypesToNofity)
                    {
                        NotifyChanges(queryTypeToNofity);
                    }
                }

                break;
            case OperationFetch operationFetch:
                if (_storage != null)
                {
                    var entities = await operationFetch.LoadFunction(_storage);
                    HashSet<Type> queryTypesToNofity = [];
                    ConcurrentDictionary<Type, HashSet<IEntity>> entitiesChanged = [];

                    foreach (var entity in entities)
                    {
                        var entityType = entity.GetType();
                        var set = _sets.GetOrAdd(entityType, []);

                        var entityKey = entity.GetKey().EnsureNotNull();
                        
                        if (set.TryGetValue(entityKey, out var localEntity))
                        {
                            if (operationFetch.CompareFunc?.Invoke(entity, localEntity) == true)
                            {
                                var entityChangesInSet = entitiesChanged.GetOrAdd(entityType, []);
                                entityChangesInSet.Add(entity);
                            }

                            set[entityKey] = entity;
                        }
                        else
                        {
                            set.Add(entityKey, entity);
                        }

                        queryTypesToNofity.Add(entityType);
                    }

                    foreach (var queryTypeToNofity in queryTypesToNofity)
                    {
                        if (entitiesChanged.TryGetValue(queryTypeToNofity, out var entityChangesInSet))
                        {
                            NotifyChanges(queryTypeToNofity, [.. entityChangesInSet]);
                        }
                        else
                        {
                            NotifyChanges(queryTypeToNofity);
                        }                        
                    }
                }
                break;
            case OperationSave operationSave:
                {
                    if (_storage != null)
                    {
                        var listOfStorageOperation = new List<StorageOperation>();
                        foreach (var pendingOperation in _pendingQueue)
                        {
                            switch (pendingOperation)
                            {
                                case OperationAdd operationAdd:
                                    listOfStorageOperation.Add(new StorageAdd(new[] { operationAdd.Entity }));
                                    break;
                                case OperationUpdate operationUpdate:
                                    listOfStorageOperation.Add(new StorageUpdate(new[] { operationUpdate.Entity }));
                                    break;
                                case OperationDelete operationRemove:
                                    listOfStorageOperation.Add(new StorageDelete(new[] { operationRemove.Entity }));
                                    break;
                                case OperationAddRange operationAddRange:
                                    listOfStorageOperation.Add(new StorageAdd(operationAddRange.Entities));
                                    break;
                                case OperationUpdateRange operationUpdateRange:
                                    listOfStorageOperation.Add(new StorageUpdate(operationUpdateRange.Entities));
                                    break;
                                case OperationDeleteRange operationDeleteRange:
                                    listOfStorageOperation.Add(new StorageDelete(operationDeleteRange.Entities));
                                    break;
                            }
                        }

                        await _storage.Save(listOfStorageOperation);
                    }

                    HashSet<Type> queryTypesToNofity = [];
                    foreach (var pendingOperation in _pendingQueue)
                    {
                        switch (pendingOperation)
                        {
                            case OperationAdd operationAdd:
                                {
                                    var entityType = operationAdd.Entity.GetType();
                                    var set = _sets.GetOrAdd(entityType, []);
                                    set.Add(operationAdd.Entity.GetKey().EnsureNotNull(), operationAdd.Entity);
                                    if (!queryTypesToNofity.Contains(entityType))
                                    {
                                        queryTypesToNofity.Add(entityType);
                                    }
                                }
                                break;
                            case OperationAddRange operationAddRage:
                                {
                                    foreach (var entity in operationAddRage.Entities)
                                    {
                                        var entityType = entity.GetType();
                                        var set = _sets.GetOrAdd(entityType, []);
                                        set.Add(entity.GetKey().EnsureNotNull(), entity);
                                        if (!queryTypesToNofity.Contains(entityType))
                                        {
                                            queryTypesToNofity.Add(entityType);
                                        }
                                    }
                                }
                                break;
                            case OperationUpdate operationUpdate:
                                break;
                            case OperationDelete operationRemove:
                                {
                                    var set = _sets.GetOrAdd(operationRemove.Entity.GetType(), []);
                                    set.Remove(operationRemove.Entity.GetKey().EnsureNotNull());

                                    if (!queryTypesToNofity.Contains(operationRemove.Entity.GetType()))
                                    {
                                        queryTypesToNofity.Add(operationRemove.Entity.GetType());
                                    }
                                }
                                break;
                            case OperationDeleteRange operationRemoveRange:
                                {
                                    foreach (var entity in operationRemoveRange.Entities)
                                    {
                                        var entityType = entity.GetType();
                                        var set = _sets.GetOrAdd(entityType, []);
                                        set.Remove(entity.GetKey().EnsureNotNull());

                                        if (!queryTypesToNofity.Contains(entityType))
                                        {
                                            queryTypesToNofity.Add(entityType);
                                        }
                                    }
                                }
                                break;
                        }
                    }

                    _pendingQueue.Clear();
                    _pendingInserts.Clear();
                    _pendingUpdates.Clear();
                    _pendingDeletes.Clear();

                    foreach (var queryTypeToNofity in queryTypesToNofity)
                    {
                        NotifyChanges(queryTypeToNofity);
                    }
                }
                break;

            case OperationFlush operationFlush:
                operationFlush.Signal.Set();
                break;
        }
    }

    public void Add(IEntity entity)
    {
        _operationsBlock.Post(new OperationAdd(entity));
    }

    public void AddRange(IEnumerable<IEntity> entities)
    {
        _operationsBlock.Post(new OperationAddRange(entities));
    }

    public void Update(IEntity entity)
    {
        if (entity.GetKey() == null)
        {
            throw new InvalidOperationException("Updating entity with uninitialized key");
        }

        _operationsBlock.Post(new OperationUpdate(entity));
    }

    public void Delete(IEntity entity)
    {
        if (entity.GetKey() == null)
        {
            throw new InvalidOperationException("Deleting entity with uninitialized key");
        }

        _operationsBlock.Post(new OperationDelete(entity));
    }

    public void DeleteRange(IEnumerable<IEntity> entities)
    {
        _operationsBlock.Post(new OperationDeleteRange(entities));
    }

    public void Load<T>(Expression<Func<IQueryable<T>, IQueryable<T>>>? predicate = null, Func<T, T, bool>? compareFunc = null) where T : class, IEntity
    {
        _operationsBlock.Post(
            new OperationFetch(
                loadFunction: storage => storage.Load(predicate?.Compile()),
                compareFunc: compareFunc != null ? (storageEntity, localEntity) => compareFunc((T)storageEntity, (T)localEntity) : null));
    }

    public void Save()
    {
        _operationsBlock.Post(new OperationSave());
    }

    public IReadOnlyList<T> Set<T>() where T : class, IEntity
    {
        var typeofT = typeof(T);
        var set = _sets.GetOrAdd(typeofT, []);

        return set.Select(_=>_.Value).Cast<T>().Concat(_pendingQueue.OfType<OperationAdd>().Select(_=>_.Entity).Cast<T>()).ToList();
    }

    public async Task Flush()
    {
        var signalEvent = new AsyncAutoResetEvent();
        _operationsBlock.Post(new OperationFlush(signalEvent));
        await signalEvent.WaitAsync();
    }

    public IQuery<T> Query<T>(Expression<Func<IQueryable<T>, IQueryable<T>>>? predicateExpression = null) where T : class, IEntity
    {
        var predicate = predicateExpression?.Compile();

        var typeofT = typeof(T);
        var queries = _queries.GetOrAdd(typeofT, []);

        var observableQuery = new ObservableQuery<T>(this, predicate);
        queries.Add(new WeakReference<IObservableQuery>(observableQuery));
        
        return observableQuery.Query;
    }

    public T? FindByKey<T>(object key) where T : class, IEntity
    {
        var typeofT = typeof(T);
        var set = _sets.GetOrAdd(typeofT, []);

        if (set.TryGetValue(key, out var entity))
        {
            return (T)entity;
        }

        return default;
    }

    private void NotifyChanges(Type typeOfEntity, params IEntity[] changedEntities)
    {
        var queries = _queries.GetOrAdd(typeOfEntity, []);

        try
        {
            _notificationSemaphore.Wait();
            List<WeakReference<IObservableQuery>>? referencesToRemove = null;
            foreach (var queryReference in queries)
            {
                if (queryReference.TryGetTarget(out var query))
                {
                    query.NotifyChanges(changedEntities);
                }
                else
                {
                    referencesToRemove ??= [];
                    referencesToRemove.Add(queryReference);
                }
            }

            if (referencesToRemove?.Count > 0)
            {
                foreach (var queryReference in referencesToRemove)
                {
                    queries.Remove(queryReference);
                }
            }
        }
        finally
        {
            _notificationSemaphore.Release();
        }

    }
}
