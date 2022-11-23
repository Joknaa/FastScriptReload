﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ImmersiveVRTools.Runtime.Common;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace QuickCodeIteration.Scripts.Runtime
{
    [PreventHotReload]
    public class AssemblyChangesLoader: IAssemblyChangesLoader
    {
        public const string ClassnamePatchedPostfix = "__Patched_";
        public const string ON_HOT_RELOAD_METHOD_NAME = "OnScriptHotReload";
        public const string ON_HOT_RELOAD_NEW_TYPE_ADDED_STATIC_METHOD_NAME = "OnScriptHotReloadNewTypeAdded";
        
        private static AssemblyChangesLoader _instance;
        public static AssemblyChangesLoader Instance => _instance ?? (_instance = new AssemblyChangesLoader());

        public void DynamicallyUpdateMethodsForCreatedAssembly(Assembly dynamicallyLoadedAssemblyWithUpdates)
        {
            var sw = new Stopwatch();
            sw.Start();
            
            //TODO: how to unload previously generated assembly?
            var allTypesInNonDynamicGeneratedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.GetCustomAttributes<DynamicallyCreatedAssemblyAttribute>().Any())
                .SelectMany(a => a.GetTypes())
                .ToList();

            var excludeMethodsDefinedOnTypes = new List<Type>
            {
                typeof(MonoBehaviour),
                typeof(Behaviour),
                typeof(UnityEngine.Object),
                typeof(Component),
                typeof(System.Object)
            }; //TODO: move out and possibly define a way to exclude all non-client created code? as this will crash editor

            const BindingFlags ALL_METHODS_BINDING_FLAGS = BindingFlags.Public | BindingFlags.NonPublic |
                                                           BindingFlags.Static | BindingFlags.Instance |
                                                           BindingFlags.FlattenHierarchy; //TODO: move out

            foreach (var createdType in dynamicallyLoadedAssemblyWithUpdates.GetTypes()
                         .Where(t => t.IsClass
                                     && !typeof(Delegate).IsAssignableFrom(t) //don't redirect delegates
                         )
                    )
            {
                if (createdType.GetCustomAttribute<PreventHotReload>() != null)
                {
                    //TODO: ideally type would be excluded from compilation not just from detour
                    Debug.Log($"Type: {createdType.Name} marked as {nameof(PreventHotReload)} - ignoring change.");
                    continue;
                }
                
                var createdTypeNameWithoutPatchedPostfix = RemoveClassPostfix(createdType.FullName);
                var matchingTypeInExistingAssemblies = allTypesInNonDynamicGeneratedAssemblies.SingleOrDefault(t => t.FullName == createdTypeNameWithoutPatchedPostfix);
                if (matchingTypeInExistingAssemblies != null)
                {
                    var allMethodsInExistingType = matchingTypeInExistingAssemblies.GetMethods(ALL_METHODS_BINDING_FLAGS)
                        .Where(m => !excludeMethodsDefinedOnTypes.Contains(m.DeclaringType))
                        .ToList();
                    foreach (var createdTypeMethodToUpdate in createdType.GetMethods(ALL_METHODS_BINDING_FLAGS)
                                 .Where(m => !excludeMethodsDefinedOnTypes.Contains(m.DeclaringType)))
                    {
                        var createdTypeMethodToUpdateFullDescriptionWithoutPatchedClassPostfix = RemoveClassPostfix(createdTypeMethodToUpdate.FullDescription());
                        var matchingMethodInExistingType = allMethodsInExistingType.SingleOrDefault(m => m.FullDescription() == createdTypeMethodToUpdateFullDescriptionWithoutPatchedClassPostfix);
                        if (matchingMethodInExistingType != null)
                        {
                            Memory.DetourMethod(matchingMethodInExistingType, createdTypeMethodToUpdate);
                        }
                        else
                        {
                            Debug.LogWarning($"Method: {createdTypeMethodToUpdate.FullDescription()} does not exist in initially compiled type: {matchingTypeInExistingAssemblies.FullName}. " +
                                             $"Adding new methods at runtime is not supported. Make sure to add method before initial compilation.");
                        }
                    }
                    
                    FindAndExecuteOnScriptHotReload(matchingTypeInExistingAssemblies);
                }
                else
                {
                    Debug.LogWarning($"Unable to find existing type for: '{createdType.FullName}', this is not an issue if you added new type");                    
                    var onScriptHotReloadStaticFnForType = createdType.GetMethod(ON_HOT_RELOAD_NEW_TYPE_ADDED_STATIC_METHOD_NAME, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (onScriptHotReloadStaticFnForType != null)
                    {
                        onScriptHotReloadStaticFnForType.Invoke(null, null);
                    }
                    
                    FindAndExecuteOnScriptHotReload(createdType);
                }
            }
            
            Debug.Log($"Hot-reload completed (took {sw.ElapsedMilliseconds}ms)");
        }

        private static void FindAndExecuteOnScriptHotReload(Type type)
        {
            var onScriptHotReloadFnForType = type.GetMethod(ON_HOT_RELOAD_METHOD_NAME, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (onScriptHotReloadFnForType != null)
            {
                UnityMainThreadDispatcher.Instance.Enqueue(() =>
                {
                    foreach (var instanceOfType in GameObject.FindObjectsOfType(type)) //TODO: perf - could find them in different way?
                    {
                        onScriptHotReloadFnForType.Invoke(instanceOfType, null);
                    }
                });
            }
        }

        private static string RemoveClassPostfix(string fqdn)
        {
            return fqdn.Replace(ClassnamePatchedPostfix, string.Empty);
        }
    }
    
    
    [AttributeUsage(AttributeTargets.Assembly)]
    public class DynamicallyCreatedAssemblyAttribute : Attribute
    {
        public string GenerationIdentifier { get; }

        public DynamicallyCreatedAssemblyAttribute(string generationIdentifier)
        {
            GenerationIdentifier = generationIdentifier;
        }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class PreventHotReload : Attribute
    {
        
    }
    
    public interface IAssemblyChangesLoader
    {
        void DynamicallyUpdateMethodsForCreatedAssembly(Assembly dynamicallyLoadedAssemblyWithUpdates);
    }
}

