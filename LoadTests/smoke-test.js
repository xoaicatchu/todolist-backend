import http from 'k6/http';
import { check } from 'k6';

// Smoke test - minimal load to verify basic functionality
export const options = {
    vus: 10,
    duration: '1m',
    thresholds: {
        'http_req_duration': ['p(95)<1000'],
        'http_req_failed': ['rate<0.01'],
    },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:3000';

export default function () {
    // Health check
    const healthRes = http.get(`${BASE_URL}/`);
    check(healthRes, { 'health check OK': (r) => r.status === 200 });

    // Create todo
    const todoId = `todo-smoke-${Date.now()}-${__VU}`;
    const createRes = http.post(`${BASE_URL}/api/sync/push`,
        JSON.stringify({
            events: [{
                eventId: `evt-${Date.now()}-${__VU}`,
                type: 'TODO_CREATED',
                todoId: todoId,
                payload: { title: 'Smoke test' },
                createdAt: Date.now(),
                synced: 0
            }]
        }),
        { headers: { 'Content-Type': 'application/json' } }
    );
    check(createRes, { 'create OK': (r) => r.status === 200 });

    // Pull todos
    const pullRes = http.get(`${BASE_URL}/api/sync/pull?since=0`);
    check(pullRes, { 'pull OK': (r) => r.status === 200 });
}
