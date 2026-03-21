import http from 'k6/http';
import { check, sleep } from 'k6';

const BASE_URL = __ENV.BASE_URL || 'http://todosync-nginx';
const TARGET_VUS = parseInt(__ENV.VUS || '1000');

function uuid() {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
    var r = Math.random() * 16 | 0, v = c == 'x' ? r : (r & 0x3 | 0x8);
    return v.toString(16);
  });
}

export const options = {
  vus: TARGET_VUS,
  duration: '30s',
};

export default function () {
  const todoId = uuid();
  const eventId = uuid();

  const pushRes = http.post(`${BASE_URL}/api/v2/sync/push`, JSON.stringify({
    Events: [{
      EventId: eventId, Type: 'TODO_CREATED', TodoId: todoId,
      Payload: { title: `K6 VU${__VU}`, priority: 'MEDIUM', dayKey: '2026-03-17' },
      CreatedAt: Date.now(), Synced: 0,
    }],
  }), { headers: { 'Content-Type': 'application/json' }, timeout: '30s' });
  check(pushRes, { 'push ok': (r) => r.status === 202 });

  const pullRes = http.get(`${BASE_URL}/api/v2/sync/pull?limit=10`, { timeout: '30s' });
  check(pullRes, { 'pull ok': (r) => r.status === 200 });

  sleep(0.1);
}
