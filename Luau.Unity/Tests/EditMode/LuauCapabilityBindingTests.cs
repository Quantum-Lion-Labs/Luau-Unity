using System;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace Luau.Unity.Tests
{
    public sealed class LuauCapabilityBindingTests
    {
        [Test]
        public async Task AnnotatedBehaviourRunsThroughGoldenAssetPath()
        {
            var ownerThreadId = Environment.CurrentManagedThreadId;
            var gameObject = new GameObject("Door");
            var asset = ScriptableObject.CreateInstance<LuauAsset>();
            using var root = LuauUnity.CreateState(new LuauUnityOptions
            {
                StateOptions = LuauStateOptions.Default with
                {
                    DefaultExecutionOptions = LuauExecutionOptions.Default with
                    {
                        WallClockLimit = TimeSpan.FromSeconds(5),
                    },
                },
                ConfigureHostApis = state => state.OpenLibrary(new CapabilityHostService()),
            });
            using var child = root.CreateSandboxedThread();
            var door = gameObject.AddComponent<CapabilityDoorBehaviour>();
            using var handle = root.CreateHandle(door);
            child["door"] = handle;
            asset.name = "@unity/capability-door.luau";
            var source =
                "door.Value = host.initialValue\n" +
                "door:Increment(2)\n" +
                "local asyncValue = door:IncrementLater(3)\n" +
                "door.Position = vector.create(1, 2, 3)\n" +
                "return door.Value, asyncValue, door.Hidden == nil";
            asset.SetSource(source, Encoding.UTF8.GetBytes(source));

            try
            {
                using var results = await child.ExecuteAsync(asset);

                Assert.That(results, Has.Length.EqualTo(3));
                Assert.That(results[0].Read<int>(), Is.EqualTo(45));
                Assert.That(results[1].Read<int>(), Is.EqualTo(45));
                Assert.That(results[2].Read<bool>(), Is.True);
                Assert.That(door.Value, Is.EqualTo(45));
                Assert.That(door.Position, Is.EqualTo(new Vector3(1, 2, 3)));
                Assert.That(door.BeforeAwaitThreadId, Is.EqualTo(ownerThreadId));
                Assert.That(door.AfterAwaitThreadId, Is.EqualTo(ownerThreadId));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void DestroyedUnityTargetFailsBeforeMemberAccess()
        {
            var gameObject = new GameObject("DestroyedDoor");
            var door = gameObject.AddComponent<CapabilityDoorBehaviour>();
            using var root = LuauUnity.CreateState();
            using var child = root.CreateSandboxedThread();
            using var handle = root.CreateHandle(door);
            child["door"] = handle;
            UnityEngine.Object.DestroyImmediate(gameObject);

            var exception = Assert.Throws<LuauManagedCallbackException>(
                () => child.DoString("return door.Value"));
            Assert.That(exception.InnerException, Is.TypeOf<MissingReferenceException>());
        }

        [Test]
        public void RepeatedCapabilityCreationAndCollectionRecoversHandleQuota()
        {
            const int iterationCount = 32;
            using var root = LuauUnity.CreateState(new LuauUnityOptions
            {
                StateOptions = LuauStateOptions.Default with
                {
                    MaxManagedHandleCount = 1,
                },
            });
            using var child = root.CreateSandboxedThread();

            for (var iteration = 0; iteration < iterationCount; iteration++)
            {
                var gameObject = new GameObject($"ChurnedDoor-{iteration}");
                var door = gameObject.AddComponent<CapabilityDoorBehaviour>();
                try
                {
                    using (var handle = root.CreateHandle(door))
                    {
                        child["door"] = handle;
                        using var result = child.DoString(
                            "door:Increment(1); return door.Value");
                        Assert.That(result, Has.Length.EqualTo(1));
                        Assert.That(result[0].Read<int>(), Is.EqualTo(1));
                        child["door"] = LuauValue.Nil;
                    }

                    root.CollectGarbage();
                }
                finally
                {
                    child["door"] = LuauValue.Nil;
                    root.CollectGarbage();
                    UnityEngine.Object.DestroyImmediate(gameObject);
                }
            }
        }
    }

    [LuauLibrary("host")]
    public partial class CapabilityHostService
    {
        [LuauMember("initialValue")]
        public int InitialValue => 40;
    }

    [LuauLibrary("Door", Exposure = LuauLibraryExposure.Capability)]
    public partial class CapabilityDoorBehaviour : MonoBehaviour
    {
        internal int BeforeAwaitThreadId { get; private set; }

        internal int AfterAwaitThreadId { get; private set; }

        [LuauMember]
        public int Value { get; set; }

        [LuauMember]
        public Vector3 Position { get; set; }

        public int Hidden => 99;

        [LuauMember]
        public void Increment(int amount)
        {
            Value += amount;
        }

        [LuauMember]
        public async ValueTask<int> IncrementLater(int amount)
        {
            BeforeAwaitThreadId = Environment.CurrentManagedThreadId;
            await Task.Yield();
            AfterAwaitThreadId = Environment.CurrentManagedThreadId;
            Value += amount;
            return Value;
        }
    }
}
