using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;

namespace GeneratedGame
{
    public class GeneratedRegistrationTests
    {
        [Test]
        public void ComponentsTheGeneratorEmitsAreKnownToEntities()
        {
            using var world = new World("TlGeneratedRegistration");
            var entities = world.EntityManager;
            var entity = entities.CreateEntity();
            entities.AddComponentData(entity, new Drift_PositionData { Value = new Position { X = 3f } });
            entities.AddComponentData(entity, new Drift_HeadingData { Value = new Heading { Degrees = 90f } });
            using var query = new EntityQueryBuilder(Allocator.Temp).WithAllRW<Drift_PositionData>().WithAll<Drift_HeadingData>().Build(entities);
            Assert.AreEqual(1, query.CalculateEntityCount());
            Assert.AreEqual(3f, entities.GetComponentData<Drift_PositionData>(entity).Value.X);
            Assert.AreEqual(90f, entities.GetComponentData<Drift_HeadingData>(entity).Value.Degrees);
        }
    }
}