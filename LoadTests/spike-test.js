import http from 'k6/http';
import { check, sleep } from 'k6';

// Spike test - sudden traffic surge
export const options = {
    stages: [
        { duration: '10s', target: 100 },   // Normal load
        { duration: '1m', target: 2000 },   // Sudden spike to 2000 users
        { duration: '3m', target: 2000 },   // Stay at 2000 users
        { duration: '1m', target: 100 },    // Drop back to normal
        { duration: '3m', target: 100 },    // Recovery period
        { duration: '10s', target: 0 },     // Ramp down
    ],
    thresholds: {
        'http_req_duration': ['p(95)<5000'],
        'http_req_failed': ['rate<0.1'],
    },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:3000';

export default function () {
    const todoId = `todo-${Date.now()}-${__VU}-${__ITER}`;

    const createPayload = JSON.stringify({
        events: [{
            eventId: `evt-${Date.now()}-${__VU}`,
            type: 'TODO_CREATED',
            todoId: todoId,
            payload: { title: `Spike test todo ${__VU}` },
            createdAt: Date.now(),
            synced: 0
        }]
    });

    http.post(`${BASE_URL}/api/sync/push`, createPayload, {
        headers: { 'Content-Type': 'application/json' }
    });

    sleep(1);

    http.get(`${BASE_URL}/api/sync/pull?since=0`);

    sleep(1);
}
