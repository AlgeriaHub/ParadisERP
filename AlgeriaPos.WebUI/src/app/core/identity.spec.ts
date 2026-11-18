import { identityProblems, localToday } from './identity';

describe('identity parts (owner, 2026-10-02: optional, but correct when filled)', () => {
  const today = '2026-10-02';

  it('accepts everything empty, or everything right', () => {
    expect(identityProblems({}, today)).toEqual({});
    expect(
      identityProblems(
        { idNumber: '109 876-543/B', idIssuer: 'دائرة باب الوادي', idIssuedOn: '2019-05-12', birthDate: '1980-03-04' },
        today,
      ),
    ).toEqual({});
  });

  it('names the wrong part only', () => {
    expect(identityProblems({ idNumber: 'Ben Ali' }, today)).toEqual({ idNumber: 'idDoc.problem.number' });
    expect(identityProblems({ idIssuer: '16/00' }, today)).toEqual({ idIssuer: 'idDoc.problem.issuer' });
    expect(identityProblems({ birthDate: '2026-10-03' }, today)).toEqual({ birthDate: 'idDoc.problem.future' });
    expect(identityProblems({ idIssuedOn: '1899-12-31' }, today)).toEqual({ idIssuedOn: 'idDoc.problem.tooOld' });
    expect(identityProblems({ idIssuedOn: '1979-01-01', birthDate: '1980-03-04' }, today)).toEqual({
      idIssuedOn: 'idDoc.problem.beforeBirth',
    });
  });

  it('reads today off the local calendar', () => {
    expect(localToday(new Date(2026, 0, 5, 23, 59))).toBe('2026-01-05');
  });
});
