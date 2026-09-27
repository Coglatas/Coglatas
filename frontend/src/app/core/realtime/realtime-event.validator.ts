import { DurableRealtimeEvent, REALTIME_EVENT_TYPES, RealtimeActor, RealtimeEventType } from './realtime.models';

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu;
const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

/** Validates only the approved durable envelope before a Facade can observe it. */
export function validateDurableRealtimeEvent(value: unknown, expectedTenantId: string): DurableRealtimeEvent | null {
  if (!isRecord(value)) {
    return null;
  }

  const actor = toActor(value['actor']),
    aggregateId = stringValue(value['aggregateId']),
    aggregateType = stringValue(value['aggregateType']),
    eventId = stringValue(value['eventId']),
    eventType = stringValue(value['eventType']),
    occurredAt = stringValue(value['occurredAt']),
    schemaVersion = value['payloadSchemaVersion'],
    tenantId = stringValue(value['tenantId']);

  if (
    !eventType ||
    !REALTIME_EVENT_TYPES.has(eventType) ||
    !isNonEmptyGuid(eventId) ||
    !isNonEmptyGuid(tenantId) ||
    tenantId !== expectedTenantId ||
    !isNonEmptyGuid(aggregateId) ||
    !aggregateType ||
    !Number.isInteger(schemaVersion) ||
    schemaVersion !== 1 ||
    !Number.isFinite(Date.parse(occurredAt)) ||
    !actor ||
    !isRecord(value['payload'])
  ) {
    return null;
  }

  const { aggregateVersion } = value;
  if (aggregateVersion !== null && aggregateVersion !== undefined &&
    (typeof aggregateVersion !== 'number' || !Number.isInteger(aggregateVersion) || aggregateVersion < 0)) {
    return null;
  }

  return {
    eventId,
    eventType: eventType as RealtimeEventType,
    payloadSchemaVersion: 1,
    occurredAt,
    tenantId,
    aggregateId,
    aggregateType,
    aggregateVersion: typeof aggregateVersion === 'number' ? aggregateVersion : null,
    actor,
    correlationId: nullableString(value['correlationId']),
    causationId: nullableString(value['causationId']),
    payload: value['payload']
  };
}

function toActor(value: unknown): RealtimeActor | null {
  if (!isRecord(value) || (value['actorType'] !== 'User' && value['actorType'] !== 'System')) {
    return null;
  }

  const actorId = value['actorId'];
  return actorId === null || actorId === undefined
    ? { actorType: value['actorType'], actorId: null }
    : typeof actorId === 'string' && isNonEmptyGuid(actorId)
      ? { actorType: value['actorType'], actorId }
      : null;
}

function isNonEmptyGuid(value: string | null): value is string {
  return value !== null && value.toLowerCase() !== EMPTY_GUID && GUID.test(value);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function stringValue(value: unknown): string | null {
  return typeof value === 'string' && value.length > 0 ? value : null;
}

function nullableString(value: unknown): string | null {
  return typeof value === 'string' ? value : null;
}
