import * as React from 'react';
import { observer } from 'mobx-react';

import {
    FormHelperText, IconButton, InputBase, Link, Paper, Table, TableBody, TableCell, TableHead, TableRow,
    TableSortLabel, Typography
} from '@material-ui/core';

import CloseIcon from '@material-ui/icons/Close';

import { IBackendClient } from '../services/IBackendClient';
import { DurableOrchestrationStatusFields } from '../states/DurableOrchestrationStatus';
import { OrchestrationLink } from './OrchestrationLink';
import { ResultsListTabState } from '../states/ResultsListTabState';
import { DfmContextType } from '../DfmContext';
import { RuntimeStatusToStyle } from '../theme';
import { renderJson } from './shared';
import { DateTimeHelpers } from '../DateTimeHelpers';

// Orchestrations list view
@observer
export class OrchestrationsList extends React.Component<{ state: ResultsListTabState, showLastEventColumn: boolean, backendClient: IBackendClient }> {

    static contextType = DfmContextType;
    context!: React.ContextType<typeof DfmContextType>;

    render(): JSX.Element {

        const state = this.props.state;

        return (<>
            
            <FormHelperText className="items-count-label">
                {!!state.orchestrations.length && (<>
                    
                    {`${state.orchestrations.length} items shown`}

                    {!!state.hiddenColumns.length && (<>

                        {`, ${state.hiddenColumns.length} columns hidden `}

                        (<Link className="unhide-button"
                            component="button"
                            variant="inherit"
                            onClick={() => state.unhide()}
                        >
                            unhide
                        </Link>)

                    </>)}

                </>)}
            </FormHelperText>

            <Paper elevation={0}>
                {this.renderTable(state)}
            </Paper>

        </>);
    }

    private renderTable(state: ResultsListTabState): JSX.Element {

        if (!state.orchestrations.length) {
            return (
                <Typography variant="h5" className="empty-table-placeholder" >
                    This list is empty
                </Typography>
            );
        }

        const visibleColumns = DurableOrchestrationStatusFields
            // hiding artificial 'lastEvent' column, when not used
            .filter(f => this.props.showLastEventColumn ? true : f !== 'lastEvent');

        return (
            <Table size="small">
                <TableHead>
                    <TableRow>
                        {visibleColumns.map(col => {

                            const onlyOneVisibleColumnLeft = visibleColumns.length <= state.hiddenColumns.length + 1;

                            return !state.hiddenColumns.includes(col) && (
                                <TableCell key={col}
                                    onMouseEnter={() => state.columnUnderMouse = col}
                                    onMouseLeave={() => state.columnUnderMouse = ''}
                                >
                                    <TableSortLabel
                                        active={state.orderBy === col}
                                        direction={state.orderByDirection}
                                        onClick={() => state.orderBy = col}
                                    >
                                        {col}
                                    </TableSortLabel>

                                    {state.columnUnderMouse === col && !onlyOneVisibleColumnLeft && (
                                        <IconButton
                                            color="inherit"
                                            size="small"
                                            className="column-hide-button"
                                            onClick={() => state.hideColumn(col)}
                                        >
                                            <CloseIcon />
                                        </IconButton>
                                    )}

                                </TableCell>
                            );
                        })}
                    </TableRow>
                </TableHead>
                <TableBody>
                    {state.orchestrations.map(orchestration => {

                        const rowStyle = RuntimeStatusToStyle(orchestration.runtimeStatus);
                        const cellStyle = { verticalAlign: 'top' };
                        return (
                            <TableRow
                                key={orchestration.instanceId}
                                style={rowStyle}
                            >
                                {!state.hiddenColumns.includes('instanceId') && (
                                    <TableCell className="instance-id-cell" style={cellStyle}>
                                        <OrchestrationLink orchestrationId={orchestration.instanceId} backendClient={this.props.backendClient} />
                                    </TableCell>
                                )}
                                {!state.hiddenColumns.includes('name') && (
                                    <TableCell className="name-cell" style={cellStyle}>
                                        {orchestration.name}
                                    </TableCell>
                                )}
                                {!state.hiddenColumns.includes('createdTime') && (
                                    <TableCell className="datetime-cell" style={cellStyle}>
                                        {this.context.formatDateTimeString(orchestration.createdTime)}
                                    </TableCell>
                                )}
                                {!state.hiddenColumns.includes('lastUpdatedTime') && (
                                    <TableCell className="datetime-cell" style={cellStyle}>
                                        {this.context.formatDateTimeString(orchestration.lastUpdatedTime)}
                                    </TableCell>
                                )}
                                {!state.hiddenColumns.includes('duration') && (
                                    <TableCell style={cellStyle}>
                                        {DateTimeHelpers.formatDuration(orchestration.duration)}
                                    </TableCell>
                                )}
                                {!state.hiddenColumns.includes('runtimeStatus') && (
                                    <TableCell style={cellStyle}>
                                        {orchestration.runtimeStatus}
                                    </TableCell>
                                )}
                                {!state.hiddenColumns.includes('lastEvent') && this.props.showLastEventColumn && (
                                    <TableCell style={cellStyle}>
                                        {orchestration.lastEvent}
                                    </TableCell>
                                )}
                                {!state.hiddenColumns.includes('input') && (
                                    <TableCell className="long-text-cell" style={cellStyle}>
                                        <InputBase
                                            className="long-text-cell-input"
                                            multiline fullWidth rowsMax={5} readOnly
                                            value={renderJson(orchestration.input)}
                                        />
                                    </TableCell>
                                )}
                                {!state.hiddenColumns.includes('output') && (
                                    <TableCell className="output-cell" style={cellStyle}>
                                        <InputBase
                                            className="long-text-cell-input"
                                            multiline fullWidth rowsMax={5} readOnly
                                            value={renderJson(orchestration.output)}
                                        />
                                    </TableCell>
                                )}
                                {!state.hiddenColumns.includes('customStatus') && (
                                    <TableCell className="output-cell" style={cellStyle}>
                                        <InputBase
                                            className="long-text-cell-input"
                                            multiline fullWidth rowsMax={5} readOnly
                                            value={renderJson(orchestration.customStatus)}
                                        />
                                    </TableCell>
                                )}
                            </TableRow>
                        );
                    })}
                </TableBody>
            </Table>
        );
    }
}