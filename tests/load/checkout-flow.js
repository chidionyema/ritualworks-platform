import http from 'k6/http';
import { check, sleep, group } from 'k6';
import { Rate, Trend } from 'k6/metrics';

// Custom metrics
const checkoutSuccess = new Rate('checkout_success_rate');
const checkoutDuration = new Trend('checkout_e2e_duration', true);

// SLO targets
const SLO_SUCCESS_RATE = 0.999; // 99.9%
const SLO_P99_MS = 30000;       // 30s

export const options = {
  scenarios: {
    // Ramp-up load test
    ramp: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '1m', target: 10 },   // warm up
        { duration: '3m', target: 50 },   // ramp to steady state
        { duration: '5m', target: 50 },   // hold steady
        { duration: '2m', target: 100 },  // push to peak
        { duration: '3m', target: 100 },  // hold peak
        { duration: '1m', target: 0 },    // drain
      ],
    },
    // Spike test (optional, run with -e SPIKE=true)
    spike: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '30s', target: 200 },
        { duration: '1m', target: 200 },
        { duration: '30s', target: 0 },
      ],
      exec: 'spike',
      env: { SPIKE: 'true' },
      startTime: '15m', // after ramp completes
    },
  },
  thresholds: {
    'checkout_success_rate': [{ threshold: `rate>${SLO_SUCCESS_RATE}`, abortOnFail: true }],
    'checkout_e2e_duration': [`p(99)<${SLO_P99_MS}`],
    'http_req_failed': ['rate<0.01'],       // <1% HTTP errors
    'http_req_duration': ['p(95)<5000'],    // 95th < 5s for individual requests
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';

// Simulates a user login and returns auth token
function getAuthToken() {
  const loginRes = http.post(`${BASE_URL}/api/authentication/login`, JSON.stringify({
    email: `loadtest+${__VU}@haworks.dev`,
    password: 'LoadTest123!',
  }), { headers: { 'Content-Type': 'application/json' } });

  if (loginRes.status === 200) {
    return JSON.parse(loginRes.body).accessToken;
  }
  // Fallback: use service token for load testing
  const svcRes = http.post(`${BASE_URL}/api/authentication/service-token`, null, {
    headers: { 'X-Service-Secret': __ENV.SERVICE_SECRET || 'load-test-secret' },
  });
  return svcRes.status === 200 ? JSON.parse(svcRes.body).accessToken : '';
}

export default function () {
  const startTime = Date.now();
  let success = false;
  const token = getAuthToken();
  const headers = {
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${token}`,
    'X-Idempotency-Key': `k6-${__VU}-${__ITER}-${Date.now()}`,
  };

  group('Checkout Flow', () => {
    // 1. Browse catalog
    group('Browse', () => {
      const products = http.get(`${BASE_URL}/api/products?skip=0&take=5`);
      check(products, { 'catalog 200': (r) => r.status === 200 });
    });

    // 2. Create reservation
    let reservationId;
    group('Reserve', () => {
      const res = http.post(`${BASE_URL}/api/checkout/reservations`, JSON.stringify({
        items: [{ productId: __ENV.PRODUCT_ID || '00000000-0000-0000-0000-000000000001', quantity: 1 }],
      }), { headers });

      if (check(res, { 'reservation created': (r) => r.status === 201 || r.status === 200 })) {
        reservationId = JSON.parse(res.body).reservationId || JSON.parse(res.body).id;
      }
    });

    // 3. Confirm reservation
    if (reservationId) {
      group('Confirm', () => {
        const res = http.post(
          `${BASE_URL}/api/checkout/reservations/${reservationId}/confirm`,
          null, { headers }
        );
        check(res, { 'confirm 200': (r) => r.status === 200 });
      });
    }

    // 4. Start checkout saga
    group('Checkout', () => {
      const res = http.post(`${BASE_URL}/api/checkout`, JSON.stringify({
        reservationId: reservationId,
      }), { headers });

      success = check(res, { 'checkout accepted': (r) => r.status === 202 || r.status === 200 });
    });
  });

  checkoutSuccess.add(success);
  checkoutDuration.add(Date.now() - startTime);
  sleep(1 + Math.random() * 2); // 1-3s think time
}

export function spike() {
  // Same flow but no think time — pure throughput stress
  const token = getAuthToken();
  const headers = {
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${token}`,
    'X-Idempotency-Key': `k6-spike-${__VU}-${__ITER}-${Date.now()}`,
  };

  const res = http.post(`${BASE_URL}/api/checkout/reservations`, JSON.stringify({
    items: [{ productId: __ENV.PRODUCT_ID || '00000000-0000-0000-0000-000000000001', quantity: 1 }],
  }), { headers });

  check(res, { 'spike reservation': (r) => r.status < 500 });
}
