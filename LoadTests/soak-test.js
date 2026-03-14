import http from 'k6/http';
import { check, sleep } from 'k6';

// Soak test - prolonged load to detect memory leaks
export const options = {
    stages: [
        { duration: '2m', target: 300 },    // Ramp up to 300 users
        { duration: '30m', target: 300 },   // Stay at 300 users for 30 minutes
        { duration: '2m', target: 0 },      // Ramp down
    ],
    thresholds: {
        'http_req_duration': ['p(95)<3000'],
        'http_req_failed': ['rate<0.05'],
    },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:3000';

export default function () {
    const todoId = `todo-soak-${Date.now()}-${__VU}-${__ITER}`;

    // Create
    const createPayload = JSON.stringify({
        events: [{
            eventId: `evt-create-${Date.now()}-${__VU}`,
            type: 'TODO_CREATED',
            todoId: todoId,
            payload: { title: `Soak test ${__VU}` },
            createdAt: Date.now(),
            synced: 0
        }]
    });

    http.post(`${BASE_URL}/api/sync/push`, createPayload, {
        headers: { 'Content-Type': 'application/json' }
    });

    sleep(2);

    // Pull
    http.get(`${BASE_URL}/api/sync/pull?since=0`);

    sleep(2);

    // Toggle
    const togglePayload = JSON.stringify({
        events: [{
            eventId: `evt-toggle-${Date.now()}-${__VU}`,
            type: 'TODO_TOGGLED',
            todoId: todoId,
            createdAt: Date.now(),
            synced: 0
        }]
    });

    http.post(`${BASE_URL}/api/sync/push`, togglePayload, {
        headers: { 'Content-Type': 'application/json' }
    });

    sleep(3);
}
