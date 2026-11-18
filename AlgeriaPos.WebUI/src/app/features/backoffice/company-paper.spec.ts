import { LOGO_MAX_BYTES } from '../print/store-documents-api.service';
import { ID_DOCUMENT_TYPES, idDocumentTypeKey, idDocumentTypeOrNull } from './customer-kind';
import { logoFileProblem } from './store-tab.component';

/**
 * The screen's side of the owner's paperwork answers of 2026-09-30: a logo file the server would
 * refuse is said before the save, and the identity paper's type is one of the three keys the
 * server takes, or nothing.
 */
describe('company paper, on screen', () => {
  it('takes a PNG, JPEG, WebP or SVG up to the cap, and says why anything else is refused', () => {
    for (const type of ['image/png', 'image/jpeg', 'image/webp', 'image/svg+xml']) {
      expect(logoFileProblem(type, 40_000)).withContext(type).toBeNull();
    }
    expect(logoFileProblem('image/png', LOGO_MAX_BYTES)).toBeNull();
    expect(logoFileProblem('image/png', LOGO_MAX_BYTES + 1)).toBe('size');
    expect(logoFileProblem('application/pdf', 10)).toBe('type');
    expect(logoFileProblem('image/gif', 10)).toBe('type');
    expect(logoFileProblem('', 10)).toBe('type');
  });

  it('sends one of the three identity papers or null, and names each in words', () => {
    expect([...ID_DOCUMENT_TYPES]).toEqual(['nationalCard', 'drivingLicence', 'passport']);
    for (const type of ID_DOCUMENT_TYPES) {
      expect(idDocumentTypeOrNull(type)).toBe(type);
      expect(idDocumentTypeKey(type)).toBe(`idDoc.type.${type}`);
    }
    expect(idDocumentTypeOrNull('')).toBeNull();
    expect(idDocumentTypeOrNull(null)).toBeNull();
    expect(idDocumentTypeOrNull('carteGrise')).toBeNull();
    expect(idDocumentTypeKey(null)).toBe('idDoc.type.none');
  });
});
