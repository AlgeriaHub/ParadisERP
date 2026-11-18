import { adminErrorMessage, problemCode, retryAfterSeconds, setErrorTranslator } from './error-message';
import { ApiError } from './api.service';

describe('adminErrorMessage', () => {
  afterEach(() => {
    // Reset translator between specs so no translator leaks to other suites.
    setErrorTranslator(null);
  });

  describe('with a translator set', () => {
    beforeEach(() => {
      setErrorTranslator((key) => `translated:${key}`);
    });

    it('returns the translated key for HTTP 401 session expired', () => {
      const error = new ApiError('Unauthorized', 401);
      expect(adminErrorMessage(error)).toBe('translated:error.sessionExpired');
    });

    it('returns the translated key for status 0 server unreachable', () => {
      const error = new ApiError('', 0);
      expect(adminErrorMessage(error)).toBe('translated:error.serverUnreachable');
    });

    it('returns the translated key for a non-API error with no message', () => {
      expect(adminErrorMessage(new Error())).toBe('translated:error.unknown');
      expect(adminErrorMessage(new Error(''))).toBe('translated:error.unknown');
      expect(adminErrorMessage({})).toBe('translated:error.unknown');
    });

    it('returns the error message when a non-API error has one', () => {
      const error = new Error('Database locked');
      expect(adminErrorMessage(error)).toBe('Database locked');
    });

    it('unwraps a server refusal arriving as a quoted JSON string', () => {
      const error = new ApiError('"Article introuvable."', 400);
      expect(adminErrorMessage(error)).toBe('Article introuvable.');
    });

    it('returns the translated key for 503 busy', () => {
      const error = new ApiError(JSON.stringify({ code: 'busy' }), 503);
      expect(adminErrorMessage(error)).toBe('translated:error.busy');
    });

    it('returns the translated key for 413 requestTooLarge', () => {
      const error = new ApiError(JSON.stringify({ code: 'requestTooLarge' }), 413);
      expect(adminErrorMessage(error)).toBe('translated:error.requestTooLarge');
    });

    it('returns the translated key for 400 credentialsTooLong', () => {
      const error = new ApiError(JSON.stringify({ code: 'credentialsTooLong' }), 400);
      expect(adminErrorMessage(error)).toBe('translated:error.credentialsTooLong');
    });

    it('replaces {n} in the translated template for 429 rateLimited', () => {
      setErrorTranslator((key) => `wait {n}s for ${key}`);
      const error = new ApiError(JSON.stringify({ code: 'rateLimited', retryAfterSeconds: 30 }), 429);
      expect(adminErrorMessage(error)).toBe('wait 30s for error.rateLimited');
    });
  });

  describe('with no translator set', () => {
    beforeEach(() => {
      setErrorTranslator(null);
    });

    it('returns the French fallback for HTTP 401', () => {
      const error = new ApiError('', 401);
      expect(adminErrorMessage(error)).toBe('Session expirée — reconnectez-vous.');
    });

    it('returns the French fallback for status 0', () => {
      const error = new ApiError('', 0);
      expect(adminErrorMessage(error)).toBe('Serveur injoignable.');
    });

    it('returns the fallback for a non-API error with no message', () => {
      expect(adminErrorMessage(new Error())).toBe('Unknown error');
      expect(adminErrorMessage(new Error(''))).toBe('Unknown error');
      expect(adminErrorMessage({})).toBe('Unknown error');
    });

    it('returns the error message when a non-API error has one', () => {
      const error = new Error('Client failure');
      expect(adminErrorMessage(error)).toBe('Client failure');
    });

    it('unwraps a server refusal arriving as a quoted JSON string', () => {
      const error = new ApiError('"Quantité insuffisante."', 400);
      expect(adminErrorMessage(error)).toBe('Quantité insuffisante.');
    });

    it('falls back to HTTP status when ApiError message is empty', () => {
      const error = new ApiError('', 500);
      expect(adminErrorMessage(error)).toBe('HTTP 500');
    });

    it('recognises 429 rateLimited with retryAfterSeconds and includes the seconds', () => {
      const error = new ApiError(JSON.stringify({ code: 'rateLimited', retryAfterSeconds: 30 }), 429);
      const message = adminErrorMessage(error);
      expect(message).toContain('30');
      expect(message).toBe('Trop de requêtes. Réessayez dans 30 secondes.');
    });

    it('recognises 429 rateLimited with missing retryAfterSeconds and defaults to 60', () => {
      const error = new ApiError(JSON.stringify({ code: 'rateLimited' }), 429);
      const message = adminErrorMessage(error);
      expect(message).toContain('60');
      expect(message).toBe('Trop de requêtes. Réessayez dans 60 secondes.');
    });

    it('recognises 429 too_many_attempts with retryAfterSeconds and includes the seconds', () => {
      const error = new ApiError(JSON.stringify({ code: 'too_many_attempts', retryAfterSeconds: 45 }), 429);
      const message = adminErrorMessage(error);
      expect(message).toContain('45');
      expect(message).toBe('Trop de tentatives de connexion. Réessayez dans 45 secondes.');
    });

    it('recognises 429 too_many_attempts with missing retryAfterSeconds and defaults to 60', () => {
      const error = new ApiError(JSON.stringify({ code: 'too_many_attempts' }), 429);
      const message = adminErrorMessage(error);
      expect(message).toContain('60');
      expect(message).toBe('Trop de tentatives de connexion. Réessayez dans 60 secondes.');
    });

    it('recognises 413 requestTooLarge', () => {
      const error = new ApiError(JSON.stringify({ code: 'requestTooLarge' }), 413);
      expect(adminErrorMessage(error)).toBe('La demande est trop volumineuse.');
    });

    it('recognises 503 busy', () => {
      const error = new ApiError(JSON.stringify({ code: 'busy' }), 503);
      expect(adminErrorMessage(error)).toBe('Le serveur est occupé. Réessayez dans un instant.');
    });

    it('recognises 400 credentialsTooLong', () => {
      const error = new ApiError(JSON.stringify({ code: 'credentialsTooLong' }), 400);
      expect(adminErrorMessage(error)).toBe("L'identifiant ou le mot de passe est trop long.");
    });

    it('falls back to the raw message for an ordinary error with an unrecognized code', () => {
      const body = JSON.stringify({ code: 'something_else' });
      const error = new ApiError(body, 400);
      expect(adminErrorMessage(error)).toBe(body);
    });
  });
});

describe('retryAfterSeconds', () => {
  it('reads retryAfterSeconds from an ApiError problem details body', () => {
    const error = new ApiError(JSON.stringify({ code: 'rateLimited', retryAfterSeconds: 30 }), 429);
    expect(retryAfterSeconds(error)).toBe(30);
  });

  it('returns null when retryAfterSeconds is missing or not a number', () => {
    expect(retryAfterSeconds(new ApiError(JSON.stringify({ code: 'rateLimited' }), 429))).toBeNull();
    expect(retryAfterSeconds(new ApiError(JSON.stringify({ retryAfterSeconds: '30' }), 429))).toBeNull();
    expect(retryAfterSeconds(new ApiError('not json', 429))).toBeNull();
    expect(retryAfterSeconds(new Error('fail'))).toBeNull();
  });
});

describe('adminErrorMessage — closed books (CLO-01)', () => {
  afterEach(() => setErrorTranslator(null));

  it('names the day the books are closed through, as dd/MM/yyyy', () => {
    setErrorTranslator(() => 'Closed through {date}.');
    const refused = new ApiError(JSON.stringify({ code: 'periodClosed', closedThrough: '2026-10-31' }), 409);
    expect(adminErrorMessage(refused)).toBe('Closed through 31/10/2026.');
  });

  it('still says it in French without a translator', () => {
    const refused = new ApiError(JSON.stringify({ code: 'periodClosed', closedThrough: '2026-09-30' }), 409);
    expect(adminErrorMessage(refused)).toContain('30/09/2026');
  });
});
